using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OfficeMcp.Api.Features.Employees;
using OfficeMcp.Api.Infrastructure.DingTalk;
using OfficeMcp.Api.Infrastructure.Errors;
using OfficeMcp.Api.Infrastructure.Time;

namespace OfficeMcp.Api.Features.Attendance;

/// <summary>按员工读取原始打卡明细，以七天分段查询并合并为工作日结果。</summary>
public sealed class AttendanceService(DingTalkClient dingTalk, EmployeeService employees, IOptions<AttendanceOptions> options)
{
    private static readonly JsonSerializerOptions DetailJsonOptions = new(JsonSerializerDefaults.Web)
        { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    /// <summary>查询包含首尾日期的范围，以受限并发查询七天分段，保留部分失败结果。</summary>
    /// <param name="startDate">北京时间的起始工作日。</param>
    /// <param name="endDate">北京时间的结束工作日，包含此日期。</param>
    /// <param name="userId">可选员工 ID；与 userName 互斥。</param>
    /// <param name="userName">可选精确匹配姓名；两者都不填时使用配置员工。</param>
    /// <param name="fullDetail">是否附上每条完整原始明细。</param>
    /// <param name="cancellationToken">客户端取消标记。</param>
    /// <returns>按工作日升序排列，每个日期恰好一个元素。</returns>
    public async Task<AttendanceResponse[]> QueryAsync(DateOnly startDate, DateOnly endDate,
        string? userId, string? userName, bool fullDetail, CancellationToken cancellationToken)
    {
        var dayCount = endDate.DayNumber - startDate.DayNumber + 1;
        if (dayCount < 1 || dayCount > options.Value.MaxQueryDays)
            throw new ArgumentOutOfRangeException(nameof(endDate), "日期范围不符合服务限制。");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.QueryTimeoutSeconds));
        var selectedId = userId ?? options.Value.UserId;
        string? selectedName = null;
        // ID 查询不依赖通讯录权限；只有姓名查询需要完整目录来判断唯一性。
        if (userName is not null)
        {
            try
            {
                var selected = await employees.ResolveNameAsync(userName, deadline.Token);
                selectedId = selected.UserId;
                selectedName = selected.Name;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new ApiRequestException(504, "attendance_query_timeout", "查询员工姓名时超时，请稍后重试。");
            }
        }

        var results = new AttendanceResponse?[dayCount];
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, (dayCount + 6) / 7), new ParallelOptions
            {
                MaxDegreeOfParallelism = options.Value.MaxParallelDays,
                CancellationToken = deadline.Token
            }, async (segment, token) =>
            {
                var offset = segment * 7;
                var length = Math.Min(7, dayCount - offset);
                try
                {
                    var days = await QuerySegmentAsync(startDate.AddDays(offset), length, selectedId,
                        selectedName, fullDetail, token);
                    // 整段完成校验后一次发布，避免损坏的后续记录留下部分成功日期。
                    for (var index = 0; index < length; index++) results[offset + index] = days[index];
                }
                catch (UpstreamException exception)
                {
                    for (var index = 0; index < length; index++)
                        results[offset + index] = CreateFailure(startDate.AddDays(offset + index), selectedId, selectedName,
                            new AttendanceError(exception.Code, exception.Message, exception.ProviderCode));
                }
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            // 保留已完成段，未完成的日期统一标记超时，不冒充无打卡记录。
        }

        cancellationToken.ThrowIfCancellationRequested();
        return results.Select((result, index) => result ?? CreateFailure(startDate.AddDays(index), selectedId, selectedName,
            new AttendanceError("attendance_query_timeout", "日期范围查询超时，此日期尚未完成，请单独重试。"))).ToArray();
    }

    /// <summary>读取一个不超过七天的闭区间，校验员工和工作日，按记录 ID 去重并整理摘要。</summary>
    /// <param name="start">当前分段的起始工作日。</param>
    /// <param name="length">当前分段天数，范围一至七天。</param>
    /// <param name="userId">已确定的完整员工 ID。</param>
    /// <param name="userName">姓名解析得到的员工姓名，可为空。</param>
    /// <param name="fullDetail">是否保留每条原始明细。</param>
    /// <param name="cancellationToken">查询截止或客户端取消标记。</param>
    private async Task<AttendanceResponse[]> QuerySegmentAsync(DateOnly start, int length, string userId,
        string? userName, bool fullDetail, CancellationToken cancellationToken)
    {
        var end = start.AddDays(length - 1);
        var rawRecords = await dingTalk.PostAttendanceRecordsAsync(new
        {
            userIds = new[] { userId },
            checkDateFrom = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 00:00:00",
            checkDateTo = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 23:59:59",
            isI18n = false
        }, cancellationToken);
        var parsed = new List<(DingTalkAttendanceDetail Value, JsonElement Raw)>();
        try
        {
            foreach (var raw in rawRecords)
            {
                var value = raw.Deserialize<DingTalkAttendanceDetail>(DetailJsonOptions)
                    ?? throw new JsonException();
                if (string.IsNullOrWhiteSpace(value.UserId) || value.WorkDate is null) throw new JsonException();
                if (value.UserId != userId)
                    throw new UpstreamException("dingtalk_user_mismatch", "钉钉返回的用户与查询用户不一致。");
                var workDate = DateOnly.FromDateTime(value.WorkDate.Value.DateTime);
                if (workDate < start || workDate > end)
                    throw new UpstreamException("dingtalk_date_mismatch", "钉钉返回的工作日不在请求分段内。");
                // full 的已知时间也必须可读；在当前错误隔离范围内拒绝无效上游时间。
                parsed.Add((value, fullDetail ? OfficeTime.NormalizeDetails(raw) : raw));
            }
        }
        catch (JsonException)
        {
            throw new UpstreamException("dingtalk_invalid_response", "钉钉打卡明细缺少有效的员工、工作日或时间字段。");
        }

        // 只合并同一个记录 ID，保留同一打卡时段的不同流水；无 ID 的记录逐条保留。
        var unique = parsed.Where(x => x.Value.Id is null).Concat(parsed.Where(x => x.Value.Id is not null)
            .GroupBy(x => x.Value.Id).Select(group => group.OrderByDescending(x => x.Value.GmtModified).First()));
        var grouped = unique.ToLookup(x => DateOnly.FromDateTime(x.Value.WorkDate!.Value.DateTime));
        return Enumerable.Range(0, length).Select(index =>
        {
            var workDate = start.AddDays(index);
            var records = grouped[workDate].OrderBy(x => x.Value.PlanCheckTime ?? x.Value.BaseCheckTime
                    ?? x.Value.UserCheckTime ?? DateTimeOffset.MaxValue)
                .ThenBy(x => x.Value.UserCheckTime).ThenBy(x => x.Value.Id)
                .Select(x => new AttendanceRecord(x.Value.CheckType, x.Value.CheckType switch
                {
                    "OnDuty" => "上班", "OffDuty" => "下班", _ => "未知类型"
                }, x.Value.PlanCheckTime ?? x.Value.BaseCheckTime, x.Value.UserCheckTime,
                    x.Value.TimeResult, TranslateStatus(x.Value.TimeResult), fullDetail ? x.Raw.Clone() : null)).ToArray();
            return new AttendanceResponse(workDate, MaskUserId(userId), records, UserName: userName);
        }).ToArray();
    }

    /// <summary>构造单日失败结果，保持所选员工及已有 ID 脱敏格式。</summary>
    /// <param name="workDate">当前失败日期。</param>
    /// <param name="userId">已选员工的完整 ID，仅在响应时脱敏。</param>
    /// <param name="userName">已解析的姓名，可为空。</param>
    /// <param name="error">可安全展示的错误。</param>
    private static AttendanceResponse CreateFailure(DateOnly workDate, string userId, string? userName, AttendanceError error) =>
        new(workDate, MaskUserId(userId), [], Success: false, Error: error, UserName: userName);

    /// <summary>保留用户 ID 首尾各三位，中间逐位替换为星号；短 ID 全部隐藏。</summary>
    /// <param name="userId">所选员工的完整 ID。</param>
    /// <returns>与原 ID 等长的脱敏文本。</returns>
    private static string MaskUserId(string userId) => userId.Length <= 6
        ? new string('*', userId.Length)
        : $"{userId[..3]}{new string('*', userId.Length - 6)}{userId[^3..]}";

    /// <summary>翻译钉钉考勤状态，不把未知或缺失状态默认为正常。</summary>
    /// <param name="code">钉钉原始状态码。</param>
    private static string TranslateStatus(string? code) => code switch
    {
        "Normal" => "正常", "Early" => "早退", "Late" => "迟到", "SeriousLate" => "严重迟到",
        "Absenteeism" => "旷工迟到", "NotSigned" => "未打卡", _ => "未知状态"
    };
}
