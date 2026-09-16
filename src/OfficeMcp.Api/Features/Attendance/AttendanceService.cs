using System.Globalization;
using Microsoft.Extensions.Options;
using OfficeMcp.Api.Infrastructure.DingTalk;
using OfficeMcp.Api.Infrastructure.Errors;

namespace OfficeMcp.Api.Features.Attendance;

/// <summary>考勤业务服务，查询固定用户并整理上下班结果。</summary>
public sealed class AttendanceService(DingTalkClient dingTalk, IOptions<AttendanceOptions> options)
{
    /// <summary>查询包含首尾日期的范围，以受限并发逐日访问钉钉，保留每天的成功或失败结果。</summary>
    /// <param name="startDate">北京时间的起始工作日。</param>
    /// <param name="endDate">北京时间的结束工作日，包含此日期。</param>
    /// <param name="cancellationToken">客户端取消标记；取消后不再继续请求钉钉。</param>
    /// <returns>按工作日升序排列的结果数组，每个日期恰好一个元素。</returns>
    public async Task<AttendanceResponse[]> QueryAsync(DateOnly startDate, DateOnly endDate,
        CancellationToken cancellationToken)
    {
        var dayCount = endDate.DayNumber - startDate.DayNumber + 1;
        if (dayCount < 1 || dayCount > options.Value.MaxQueryDays)
            throw new ArgumentOutOfRangeException(nameof(endDate), "日期范围不符合服务限制。");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.QueryTimeoutSeconds));
        var results = new AttendanceResponse?[dayCount];
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, dayCount), new ParallelOptions
            {
                MaxDegreeOfParallelism = options.Value.MaxParallelDays,
                CancellationToken = deadline.Token
            }, async (index, token) =>
            {
                // 按日期索引写入，避免完成先后顺序打乱响应，也避免结束日期递增溢出。
                var workDate = startDate.AddDays(index);
                try
                {
                    results[index] = await QueryDayAsync(workDate, token);
                }
                catch (UpstreamException exception)
                {
                    // 上游失败仅影响当前日期，其他日期继续查询，空数据和失败明确区分。
                    results[index] = CreateFailure(workDate,
                        new AttendanceError(exception.Code, exception.Message, exception.ProviderCode));
                }
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            // 整次查询超时后，保留已完成结果；未完成的日期在下面统一标记为超时。
        }

        cancellationToken.ThrowIfCancellationRequested();
        return results.Select((result, index) => result ?? CreateFailure(startDate.AddDays(index),
            new AttendanceError("attendance_query_timeout", "日期范围查询超时，此日期尚未完成，请单独重试。"))).ToArray();
    }

    /// <summary>读取指定工作日全部结果，保留缺卡、未知状态和跨天记录。</summary>
    /// <param name="workDate">北京时间对应的工作日。</param>
    /// <param name="cancellationToken">HTTP 请求取消标记。</param>
    private async Task<AttendanceResponse> QueryDayAsync(DateOnly workDate, CancellationToken cancellationToken)
    {
        var userId = options.Value.UserId;
        var result = await dingTalk.PostAsync<DingTalkAttendanceResult>("topapi/attendance/getupdatedata", new
        {
            userid = userId,
            work_date = workDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 00:00:00"
        }, cancellationToken);
        if (result.UserId is not null && result.UserId != userId)
            throw new UpstreamException("dingtalk_user_mismatch", "钉钉返回的用户与查询用户不一致。");

        // 使用打卡结果而非原始流水：同一时段多次打卡由钉钉完成业务判定。
        var records = (result.Records ?? []).OrderBy(x => x.PlannedCheckTime ?? x.ActualCheckTime ?? DateTimeOffset.MaxValue)
            .Select(x => new AttendanceRecord(x.CheckType, x.CheckType switch
            {
                "OnDuty" => "上班", "OffDuty" => "下班", _ => "未知类型"
            }, x.PlannedCheckTime, x.ActualCheckTime, x.TimeResult, TranslateStatus(x.TimeResult))).ToArray();
        // 仅在生成对外响应时脱敏，钉钉查询和用户一致性校验仍使用完整 ID。
        return new AttendanceResponse(workDate, MaskUserId(userId), "Asia/Shanghai", records);
    }

    /// <summary>构造单日失败结果，失败时也保持用户 ID 脱敏。</summary>
    /// <param name="workDate">发生失败的工作日。</param>
    /// <param name="error">可安全返回给调用者的错误信息。</param>
    private AttendanceResponse CreateFailure(DateOnly workDate, AttendanceError error) =>
        new(workDate, MaskUserId(options.Value.UserId), "Asia/Shanghai", [], Success: false, Error: error);

    /// <summary>保留用户 ID 首尾各三位，中间逐位替换为星号；短 ID 全部隐藏。</summary>
    /// <param name="userId">服务配置的完整钉钉用户 ID。</param>
    /// <returns>与原 ID 等长的脱敏字符串。</returns>
    private static string MaskUserId(string userId)
    {
        // 长度不足以保留首尾时全部隐藏，避免返回完整的短 ID。
        return userId.Length <= 6
            ? new string('*', userId.Length)
            : $"{userId[..3]}{new string('*', userId.Length - 6)}{userId[^3..]}";
    }

    /// <summary>翻译钉钉考勤状态；不把未知或缺失状态默认为正常。</summary>
    private static string TranslateStatus(string? code) => code switch
    {
        "Normal" => "正常",
        "Early" => "早退",
        "Late" => "迟到",
        "SeriousLate" => "严重迟到",
        "Absenteeism" => "旷工迟到",
        "NotSigned" => "未打卡",
        null or "" => "未知状态",
        _ => "未知状态"
    };
}
