using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OfficeMcp.Api.Features.DeviceCommands;
using OfficeMcp.Api.Infrastructure.Errors;

namespace OfficeMcp.Api.Features.Attendance;

public sealed partial class ClockInService
{
    /// <summary>接受已执行的本地上班事实，直接创建仅核验任务；不读取事前基线、不下发设备动作。</summary>
    /// <param name="deviceId">经设备认证的路由身份，必须与服务端考勤设备绑定一致。</param>
    /// <param name="report">已持久化的执行事实；重试不能改变这些字段。</param>
    /// <param name="token">HTTP 请求取消标记，任务保存后不受断开影响。</param>
    public async Task<ClockInTaskResponse> ReportLocalAsync(string deviceId, ScheduledClockInReport report, CancellationToken token)
    {
        EnsureEnabled();
        if (deviceId != options.Value.DeviceId)
            throw new ApiRequestException(403, "attendance_device_forbidden", "该设备未绑定考勤员工。");
        if (!Guid.TryParse(report.LocalRunId, out var runId))
            throw new ApiRequestException(400, "invalid_local_run_id", "local_run_id 必须是动作前持久化的 UUID。");
        if (report.CheckType != "OnDuty")
            throw new ApiRequestException(400, "invalid_check_type", "本地核验当前只支持 OnDuty。");
        if (report.WorkDate?.Length != 10 || !DateOnly.TryParseExact(report.WorkDate, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var workDate))
            throw new ApiRequestException(400, "invalid_work_date", "work_date 必须为 yyyy-MM-dd 日期。");
        if (report.Outcome is not ("launch_requested" or "failed" or "uncertain" or "expired" or "busy")
            || report.ErrorCode is not null && !Regex.IsMatch(report.ErrorCode, "^[a-z_]{1,64}$"))
            throw new ApiRequestException(400, "invalid_device_outcome", "设备动作结果或安全错误码无效。");
        var executedAt = LocalTimestamp(report.ExecutedAtUnixMs);
        var stages = new[] { report.ScreenOnAtUnixMs, report.AppRequestedAtUnixMs, report.CompletedAtUnixMs };
        var previous = report.ExecutedAtUnixMs;
        foreach (var stage in stages)
        {
            if (stage is null) continue;
            _ = LocalTimestamp(stage.Value);
            if (stage < previous) throw new ApiRequestException(400, "invalid_execution_time", "设备执行阶段时间顺序无效。");
            previous = stage.Value;
        }
        if (report.Outcome == "launch_requested" && report.AppRequestedAtUnixMs is null)
            throw new ApiRequestException(400, "invalid_execution_time", "启动成功回执必须提供实际启动请求时间。");
        if (DateOnly.FromDateTime(executedAt.ToOffset(TimeSpan.FromHours(8)).DateTime) != workDate)
            throw new ApiRequestException(400, "invalid_work_date", "执行时间与北京时间工作日不一致。");

        report = report with { LocalRunId = runId.ToString("N") };
        // 命名空间隔离本地执行与远程 request_id；员工配置也是幂等语义的一部分。
        var requestId = $"{DeviceCommandJob.LocalSchedule}:{deviceId}:{runId:N}";
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{requestId}|{attendance.Value.UserId}|{JsonSerializer.Serialize(report, DeviceCommandStore.JsonOptions)}")));
        var prior = await store.FindRequestAsync(requestId, token);
        if (prior is not null)
        {
            if (prior.Fingerprint != fingerprint)
                throw new ApiRequestException(409, "local_run_id_conflict", "同一 local_run_id 的执行事实不可更改。");
            return await ToResponseAsync(prior, token);
        }
        // 仅首次接受要求当天；跨日重传已接受的事实仍返回原任务，避免成功响应丢失后无法归档。
        var now = clock.GetUtcNow();
        if (workDate != DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(8)).DateTime))
            throw new ApiRequestException(400, "local_report_date_expired", "仅接受北京时间当天的首次执行补报。");
        if (LocalTimestamp(previous) > now.AddSeconds(30))
            throw new ApiRequestException(400, "device_clock_ahead", "手机执行时间超前服务器超过 30 秒。");

        var job = new DeviceCommandJob
        {
            Id = Guid.NewGuid().ToString("N"), RequestId = requestId, Fingerprint = fingerprint,
            DeviceId = deviceId, Kind = Kind, Action = "verify_attendance", Source = DeviceCommandJob.LocalSchedule,
            State = "verifying", CreatedAt = now, ExpiresAt = executedAt,
            VerificationDeadline = now.AddSeconds(options.Value.VerificationTimeoutSeconds),
            VerificationTimeoutSeconds = options.Value.VerificationTimeoutSeconds,
            DeviceOutcome = report.Outcome, DeviceError = report.ErrorCode,
            Payload = JsonSerializer.SerializeToElement(new { }),
            Metadata = JsonSerializer.SerializeToElement(new ClockInMetadata(report.WorkDate, report.CheckType,
                attendance.Value.UserId, null, report), DeviceCommandStore.JsonOptions)
        };
        job = await store.CreateAsync(job, token);
        return await ToResponseAsync(job, token);
    }

    /// <summary>手机只读查询本设备任务；返回摘要，绝不暴露领取令牌或内部用户基线。</summary>
    public async Task<ClockInTaskResponse> GetDeviceTaskAsync(string deviceId, string taskId, CancellationToken token)
    {
        EnsureEnabled();
        var job = await store.GetAsync(taskId, token);
        if (job is null || job.DeviceId != deviceId || job.Kind != Kind)
            throw new ApiRequestException(404, "task_not_found", "本设备的打卡任务不存在。");
        return await ToResponseAsync(job, token);
    }

    /// <summary>严格解析有限 Unix 毫秒范围，让协议错误返回 400 而非服务器异常。</summary>
    private static DateTimeOffset LocalTimestamp(long value)
    {
        if (value < 0 || value > 253402271999999)
            throw new ApiRequestException(400, "invalid_execution_time", "执行时间必须是有效的 UTC Unix 毫秒时间戳。");
        return DateTimeOffset.FromUnixTimeMilliseconds(value);
    }

    /// <summary>无事前基线时只判定日期和执行时间关系；迟到可以确认，未知状态保留为未确认。</summary>
    private static string LocalRecordRelation(DeviceCommandJob job, ClockInMetadata metadata, ClockInSnapshot? current, DateTimeOffset now)
    {
        if (current?.ActualTime is null) return "no_record";
        if (!IsRecorded(current)) return "unknown_status";
        if (metadata.LocalExecution is null) return "execution_context_missing";
        var actual = current.ActualTime.Value;
        if (actual.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != metadata.WorkDate)
            return "outside_execution_window";
        var executed = LocalTimestamp(metadata.LocalExecution.ExecutedAtUnixMs);
        if (actual > now.AddSeconds(30) || actual > executed.AddSeconds(job.VerificationTimeoutSeconds + 30))
            return "outside_execution_window";
        return actual < executed.AddSeconds(-30) ? "before_execution" : "within_execution_window";
    }
}
