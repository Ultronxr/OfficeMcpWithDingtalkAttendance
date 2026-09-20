using System.Text.Json.Serialization;

namespace OfficeMcp.Api.Features.Attendance;

/// <summary>固定作息和加班门槛，启动时读取配置；不改变钉钉原考勤状态或手机调度。</summary>
public sealed class OvertimeOptions
{
    public TimeOnly WorkStartTime { get; set; } = new(9, 0);
    public TimeOnly WorkEndTime { get; set; } = new(18, 0);
    public TimeOnly ThresholdTime { get; set; } = new(21, 0);
}

/// <summary>本次查询使用的配置快照，明确门槛包含边界，达到后从正常下班时间开始计时。</summary>
/// <param name="WorkStartTime">配置的正常上班时刻，仅展示，不校验上班卡。</param>
/// <param name="WorkEndTime">正常下班时刻，也是加班时长的计算起点。</param>
/// <param name="ThresholdTime">满足加班筛选的最早时刻，包含该时刻。</param>
/// <param name="ThresholdInclusive">恒为 true，达到门槛即计入。</param>
/// <param name="DurationBasis">恒为 work_end，表示从正常下班时间计算。</param>
public sealed record OvertimeRule(TimeOnly WorkStartTime, TimeOnly WorkEndTime, TimeOnly ThresholdTime,
    bool ThresholdInclusive = true, string DurationBasis = "work_end");

/// <summary>加班范围结果；days 只包含加班日，错误与不完整标记独立返回。</summary>
/// <param name="StartDate">请求起始工作日。</param>
/// <param name="EndDate">请求结束工作日。</param>
/// <param name="TimeZone">固定 Asia/Shanghai。</param>
/// <param name="Rule">本次配置口径。</param>
/// <param name="Days">按工作日升序排列的加班日及全部原考勤记录。</param>
/// <param name="Summary">成功查询日期中的加班汇总。</param>
/// <param name="Complete">全部请求日期均查询成功才为 true；false 时不能把汇总看作完整统计。</param>
/// <param name="Errors">原考勤查询失败日期及安全错误，不混入加班日。</param>
public sealed record OvertimeResponse(DateOnly StartDate, DateOnly EndDate, string TimeZone, OvertimeRule Rule,
    IReadOnlyList<OvertimeDay> Days, OvertimeSummary Summary, bool Complete, IReadOnlyList<OvertimeQueryError> Errors);

/// <summary>在原考勤日摘要基础上附加加班数据，保留该日全部 records 和可用姓名。</summary>
public sealed record OvertimeDay(DateOnly WorkDate, string UserId, string TimeZone,
    IReadOnlyList<AttendanceRecord> Records, OvertimeInfo Overtime, bool Success = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserName = null);

/// <summary>按所属工作日构造的固定作息和加班时间，跨午夜仍使用完整时间戳相减。</summary>
/// <param name="NormalWorkStart">该工作日配置的正常上班时间。</param>
/// <param name="NormalWorkEnd">该工作日配置的正常下班时间，也是计时起点。</param>
/// <param name="ThresholdAt">该工作日的加班筛选门槛。</param>
/// <param name="LastOffDutyAt">当天全部有效下班时间中的最晚一次，不依赖钉钉状态码或计划时间。</param>
/// <param name="OvertimeSeconds">从正常下班至实际下班的精确秒数，保留上游子秒精度。</param>
/// <param name="OvertimeHours">展示用小时数，四舍五入到两位小数，汇总不使用逐日舍入值相加。</param>
public sealed record OvertimeInfo(DateTimeOffset NormalWorkStart, DateTimeOffset NormalWorkEnd,
    DateTimeOffset ThresholdAt, DateTimeOffset LastOffDutyAt, decimal OvertimeSeconds, decimal OvertimeHours);

/// <summary>仅汇总成功查询日期；先合计精确秒数，再换算展示小时，避免逐日舍入误差。</summary>
public sealed record OvertimeSummary(int OvertimeDays, decimal TotalOvertimeSeconds, decimal TotalOvertimeHours);

/// <summary>单日查询失败，不代表缺卡或没有加班。</summary>
public sealed record OvertimeQueryError(DateOnly WorkDate, string UserId, AttendanceError Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserName = null);
