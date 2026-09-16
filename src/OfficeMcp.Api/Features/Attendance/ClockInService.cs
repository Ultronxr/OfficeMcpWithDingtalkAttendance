using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OfficeMcp.Api.Features.DeviceCommands;
using OfficeMcp.Api.Infrastructure.DingTalk;
using OfficeMcp.Api.Infrastructure.Errors;

namespace OfficeMcp.Api.Features.Attendance;

/// <summary>协调远程动作与官方考勤核验，不把设备启动应用的回执当作业务成功。</summary>
public sealed class ClockInService(DeviceCommandStore store, DingTalkClient dingTalk,
    IOptions<ClockInOptions> options, IOptions<AttendanceOptions> attendance,
    IOptions<DeviceCommandOptions> devices, TimeProvider clock)
{
    public const string Kind = "attendance.clock_in";

    /// <summary>校验请求、读取幂等记录与考勤基线，然后持久化一个固定动作。</summary>
    public async Task<ClockInTaskResponse> CreateAsync(ClockInRequest request, CancellationToken token)
    {
        EnsureEnabled();
        if (!Guid.TryParse(request.RequestId, out var requestId))
            throw new ApiRequestException(400, "invalid_request_id", "request_id 必须是 UUID；重试同一操作时复用原值。");
        if (request.CheckType is not ("OnDuty" or "OffDuty"))
            throw new ApiRequestException(400, "invalid_check_type", "check_type 必须为 OnDuty 或 OffDuty。");
        if (request.WaitSeconds is < 0 or > 60)
            throw new ApiRequestException(400, "invalid_wait_seconds", "wait_seconds 必须在 0 至 60 秒之间。");
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(8)).DateTime);
        var workDate = today;
        if (request.WorkDate is not null && (request.WorkDate.Length != 10 || !DateOnly.TryParseExact(request.WorkDate,
            "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out workDate)))
            throw new ApiRequestException(400, "invalid_work_date", "work_date 必须为 yyyy-MM-dd 日期。");
        // 手机只执行现在的动作；前一天仅允许核验跨午夜的下班时段。
        if (workDate != today && !(request.CheckType == "OffDuty" && workDate == today.AddDays(-1)))
            throw new ApiRequestException(400, "invalid_clock_in_date", "远程动作只支持当前工作日，或前一天跨午夜的下班时段。");
        var dateText = workDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Kind}|{options.Value.DeviceId}|{attendance.Value.UserId}|{dateText}|{request.CheckType}")));
        var prior = await store.FindRequestAsync(requestId.ToString("N"), token);
        if (prior is not null)
        {
            if (prior.Fingerprint != fingerprint) throw new ApiRequestException(409, "request_id_conflict", "同一 request_id 不能用于不同操作。");
            return await WaitAsync(prior.Id, request.WaitSeconds, token);
        }

        var baseline = await ReadSnapshotAsync(dateText, request.CheckType, token);
        if (workDate != today && baseline?.PlannedTime is null)
            throw new ApiRequestException(400, "cross_day_schedule_required", "核验前一天的下班卡需要明确的跨天排班记录。");
        if (workDate != today && DateOnly.FromDateTime(baseline!.PlannedTime!.Value.ToOffset(TimeSpan.FromHours(8)).DateTime) != today)
            throw new ApiRequestException(400, "cross_day_schedule_required", "该下班时段不属于当前日期的跨天排班。");

        var alreadyDone = request.CheckType == "OnDuty" && IsRecorded(baseline);
        var metadata = new ClockInMetadata(dateText, request.CheckType, attendance.Value.UserId, baseline);
        var job = new DeviceCommandJob
        {
            Id = Guid.NewGuid().ToString("N"), RequestId = requestId.ToString("N"), Fingerprint = fingerprint,
            DeviceId = options.Value.DeviceId, Kind = Kind, Action = "wake_dingtalk",
            State = alreadyDone ? "already_completed" : "queued", CreatedAt = now,
            ExpiresAt = now.AddSeconds(options.Value.CommandTtlSeconds), FinishedAt = alreadyDone ? now : null,
            ExecutionTimeoutSeconds = options.Value.ExecutionTimeoutSeconds,
            VerificationTimeoutSeconds = options.Value.VerificationTimeoutSeconds,
            Payload = JsonSerializer.SerializeToElement(new { work_date = dateText, check_type = request.CheckType }),
            Metadata = JsonSerializer.SerializeToElement(metadata, DeviceCommandStore.JsonOptions),
            Result = alreadyDone ? JsonSerializer.SerializeToElement(ToRecord(request.CheckType, baseline!), DeviceCommandStore.JsonOptions) : null
        };
        job = await store.CreateAsync(job, token);
        return await WaitAsync(job.Id, request.WaitSeconds, token);
    }

    /// <summary>在当前 HTTP 调用内有界等待；断开连接只结束等待，已创建任务由后台继续核验。</summary>
    private async Task<ClockInTaskResponse> WaitAsync(string id, int seconds, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var job = await store.GetAsync(id, token) ?? throw new ApiRequestException(404, "task_not_found", "任务不存在。");
            if (job.IsTerminal || elapsed.Elapsed.TotalSeconds >= seconds) return await ToResponseAsync(job, token);
            await Task.Delay(400, token);
        }
    }

    /// <summary>获取单个任务状态，不触发新的手机动作。</summary>
    public async Task<ClockInTaskResponse> GetAsync(string id, CancellationToken token)
    {
        EnsureEnabled();
        var job = await store.GetAsync(id, token);
        if (job is null || job.Kind != Kind) throw new ApiRequestException(404, "task_not_found", "远程打卡任务不存在。");
        return await ToResponseAsync(job, token);
    }

    /// <summary>为后台处理器读取当天指定类型的唯一时段，拒绝不明确的多班次。</summary>
    private async Task<ClockInSnapshot?> ReadSnapshotAsync(string date, string checkType, CancellationToken token)
    {
        var result = await dingTalk.PostAsync<DingTalkAttendanceResult>("topapi/attendance/getupdatedata",
            new { userid = attendance.Value.UserId, work_date = date + " 00:00:00" }, token);
        if (result.UserId is not null && result.UserId != attendance.Value.UserId)
            throw new UpstreamException("dingtalk_user_mismatch", "钉钉返回的用户与配置用户不一致。");
        var matches = (result.Records ?? []).Where(x => x.CheckType == checkType).ToArray();
        if (matches.Length > 1) throw new ApiRequestException(409, "ambiguous_attendance_period", "发现多个同类型考勤时段，当前远程流程只支持每日一次上班和一次下班。");
        var record = matches.SingleOrDefault();
        return record is null ? null : new ClockInSnapshot(record.RecordId, record.PlanId,
            record.PlannedCheckTime, record.ActualCheckTime, record.TimeResult);
    }

    /// <summary>检查一次任务状态；领取回执不明时只读取钉钉，不重新向手机发送动作。</summary>
    public async Task ProcessAsync(DeviceCommandJob job, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        if (job.State == "queued")
        {
            if (now >= job.ExpiresAt) await FinishAsync(job.Id, "expired", null, "命令未被设备及时领取，已过期，不会补执行。", token);
            return;
        }
        if (job.State == "claimed" && now < job.ClaimedAt!.Value.AddSeconds(job.ExecutionTimeoutSeconds)) return;
        if (now >= job.VerificationDeadline!.Value)
        {
            await FinishAsync(job.Id, "unconfirmed", null, "核验期限内未能确认新的打卡记录；不要据此断言实际未打卡。", token);
            return;
        }
        await store.UpdateAsync(job.Id, current => current.IsTerminal ? current : current with { State = "verifying" }, token);
        var metadata = job.Metadata.Deserialize<ClockInMetadata>(DeviceCommandStore.JsonOptions)!;
        if (metadata.UserId != attendance.Value.UserId)
        {
            await FinishAsync(job.Id, "unconfirmed", null, "固定用户配置已改变，停止核验原用户任务。", token);
            return;
        }
        // 单次读取也受任务剩余核验时间约束，避免网络等待拖过整个任务的期限。
        using var verificationBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
        var remaining = job.VerificationDeadline!.Value - clock.GetUtcNow();
        verificationBudget.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        try
        {
            var current = await ReadSnapshotAsync(metadata.WorkDate, metadata.CheckType, verificationBudget.Token);
            if (ConfirmsNewRecord(job, metadata.Baseline, current, clock.GetUtcNow()))
            {
                await FinishAsync(job.Id, "succeeded", ToRecord(metadata.CheckType, current!),
                    metadata.CheckType == "OffDuty" && IsRecorded(metadata.Baseline)
                        ? "已通过钉钉 API 确认下班打卡时间更新。" : "已通过钉钉 API 确认对应打卡记录。", token);
            }
            else if (job.DeviceOutcome is "failed" or "busy" or "expired")
                await FinishAsync(job.Id, "failed", null, $"手机动作未完成（{job.DeviceError ?? job.DeviceOutcome}），也未核验到新记录。", token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && verificationBudget.IsCancellationRequested)
        {
            await FinishAsync(job.Id, "unconfirmed", null, "核验时间已用尽，尚未确认对应打卡记录。", token);
        }
        catch (UpstreamException exception)
        {
            // 接口暂时不可用时继续在后台重试读取，不重复触发设备动作。
            await store.UpdateAsync(job.Id, current => current.IsTerminal ? current : current with
            {
                LastError = $"核验暂未完成：{exception.Code}，钉钉错误码 {exception.ProviderCode ?? "无"}。"
            }, token);
        }
        catch (ApiRequestException exception)
        {
            await FinishAsync(job.Id, "unconfirmed", null, exception.Message, token);
        }
    }

    /// <summary>要求目标时段一致且实际时间有新变化，不把基线中的旧记录当成执行结果。</summary>
    private static bool ConfirmsNewRecord(DeviceCommandJob job, ClockInSnapshot? baseline, ClockInSnapshot? current, DateTimeOffset now)
    {
        if (!IsRecorded(current) || current!.ActualTime!.Value < job.CreatedAt.AddSeconds(-30)
            || current.ActualTime.Value > now.AddSeconds(30)) return false;
        if (baseline?.PlanId is > 0 && current.PlanId != baseline.PlanId) return false;
        if (baseline?.PlannedTime is not null && current.PlannedTime != baseline.PlannedTime) return false;
        // 下班更新必须实际时间严格增大，仅记录 ID 变化不足以证明更新成功。
        return !IsRecorded(baseline) || current.ActualTime!.Value > baseline!.ActualTime!.Value;
    }

    /// <summary>记录存在与考勤是否正常分开判断，迟到和早退也可能已经完成打卡。</summary>
    private static bool IsRecorded(ClockInSnapshot? value) => value?.ActualTime is not null
        && value.StatusCode is "Normal" or "Early" or "Late" or "SeriousLate" or "Absenteeism";

    /// <summary>将内部快照转换为已有的公开记录格式。</summary>
    private static AttendanceRecord ToRecord(string type, ClockInSnapshot value) => new(type,
        type == "OnDuty" ? "上班" : "下班", value.PlannedTime, value.ActualTime, value.StatusCode,
        value.StatusCode switch
        {
            "Normal" => "正常", "Early" => "早退", "Late" => "迟到", "SeriousLate" => "严重迟到",
            "Absenteeism" => "旷工迟到", "NotSigned" => "未打卡", _ => "未知状态"
        });

    /// <summary>原子写入最终结果，不覆盖另一个请求已经保存的终态。</summary>
    private Task<DeviceCommandJob> FinishAsync(string id, string state, AttendanceRecord? record, string message,
        CancellationToken token) => store.UpdateAsync(id, current => current.IsTerminal ? current : current with
    {
        State = state, FinishedAt = clock.GetUtcNow(), LastError = message,
        Result = record is null ? null : JsonSerializer.SerializeToElement(record, DeviceCommandStore.JsonOptions)
    }, token);

    /// <summary>返回不含基线、领取令牌和完整用户 ID 的任务摘要。</summary>
    private async Task<ClockInTaskResponse> ToResponseAsync(DeviceCommandJob job, CancellationToken token)
    {
        var metadata = job.Metadata.Deserialize<ClockInMetadata>(DeviceCommandStore.JsonOptions)!;
        var id = metadata.UserId;
        var masked = id.Length <= 6 ? new string('*', id.Length) : id[..3] + new string('*', id.Length - 6) + id[^3..];
        return new ClockInTaskResponse(job.Id, job.State, job.IsTerminal, job.State is "succeeded" or "already_completed",
            metadata.WorkDate, metadata.CheckType, masked, await store.IsOnlineAsync(job.DeviceId, token), job.ExpiresAt,
            job.DeviceOutcome, job.Result?.Deserialize<AttendanceRecord>(DeviceCommandStore.JsonOptions),
            job.State == "already_completed" ? "已有上班打卡记录，未再次下发手机动作。" : job.LastError);
    }

    /// <summary>未启用或未登记设备时阻止创建动作，原有考勤查询仍可继续使用。</summary>
    private void EnsureEnabled()
    {
        if (!devices.Value.Enabled || !devices.Value.Devices.ContainsKey(options.Value.DeviceId))
            throw new ApiRequestException(503, "remote_device_not_configured", "远程设备功能未启用或设备未登记。");
    }
}
