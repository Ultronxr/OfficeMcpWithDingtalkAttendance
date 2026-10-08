using System.Text.Json.Serialization;

namespace OfficeMcp.Api.Features.Attendance;

/// <summary>固定作息和加班门槛，启动时读取配置；不改变钉钉原考勤状态或手机调度。</summary>
public sealed class OvertimeOptions
{
    public TimeOnly WorkStartTime { get; set; } = new(9, 0);
    public TimeOnly WorkEndTime { get; set; } = new(18, 0);
    public TimeOnly ThresholdTime { get; set; } = new(21, 0);
}

/// <summary>工作日配置与非工作日返回规则快照，明确不同日期的筛选和时长口径。</summary>
/// <param name="WorkStartTime">配置的正常上班时刻，仅展示，不校验上班卡。</param>
/// <param name="WorkEndTime">正常下班时刻，也是加班时长的计算起点。</param>
/// <param name="ThresholdTime">满足加班筛选的最早时刻，包含该时刻。</param>
/// <param name="ThresholdInclusive">恒为 true，达到门槛即计入。</param>
/// <param name="DurationBasis">工作日为 work_end，表示从正常下班时间计算。</param>
/// <param name="NonWorkdayInclusion">any_record，非工作日任意记录均流出，缺卡不隐藏。</param>
/// <param name="NonWorkdayDurationBasis">first_on_to_last_off，完整卡从最早上班至最晚下班，不扣餐休。</param>
/// <param name="UnknownClassificationPolicy">return_for_review，分类未知保留记录并标记待核验。</param>
public sealed record OvertimeRule(TimeOnly WorkStartTime, TimeOnly WorkEndTime, TimeOnly ThresholdTime,
    bool ThresholdInclusive = true, string DurationBasis = "work_end", string NonWorkdayInclusion = "any_record",
    string NonWorkdayDurationBasis = "first_on_to_last_off", string UnknownClassificationPolicy = "return_for_review");

/// <summary>加班与待核验出勤范围结果；保留异常日期，错误与不完整标记独立返回。</summary>
/// <param name="StartDate">请求起始工作日。</param>
/// <param name="EndDate">请求结束工作日。</param>
/// <param name="Rule">本次配置口径。</param>
/// <param name="Days">按工作日升序排列的加班日及分类未知候选，包含缺卡并保留全部记录。</param>
/// <param name="Summary">已确认加班和可核算时长的部分汇总，明确异常与待核验数量。</param>
/// <param name="Complete">考勤与所需日期分类均查询成功才为 true；时长是否齐全另看 summary.duration_complete。</param>
/// <param name="Errors">考勤与分类的逐日安全错误；分类失败的已有记录仍保留在 days。</param>
public sealed record OvertimeResponse(DateOnly StartDate, DateOnly EndDate, OvertimeRule Rule,
    IReadOnlyList<OvertimeDay> Days, OvertimeSummary Summary, bool Complete, IReadOnlyList<OvertimeQueryError> Errors);

/// <summary>加班或待核验出勤日；异常只标记、不隐藏，只有 OvertimeConfirmed=true 才计入已确认天数。</summary>
/// <param name="WorkDate">钉钉工作日期，跨午夜记录仍按原日期归属。</param>
/// <param name="UserId">脱敏员工 ID。</param>
/// <param name="Records">该日期全部记录，不能只返回用于计算的首末两卡。</param>
/// <param name="Overtime">可核算时长及实际时间，未知值为 null。</param>
/// <param name="DayType">workday、non_workday（含安排休息的节假日）或 unknown。</param>
/// <param name="OvertimeConfirmed">按本工具规则确认加班；null 表示待核验，不等于钉钉审批认定。</param>
/// <param name="InclusionReason">workday_threshold、non_workday_record 或 classification_unavailable。</param>
/// <param name="Classification">钉钉原生休息安排的分类来源、状态和错误。</param>
/// <param name="Anomalies">缺卡、未知类型、无实际时间、逆序或分类不可用的异常列表。</param>
/// <param name="Success">该日期的原考勤查询成功，不表示分类或时长必然完整。</param>
/// <param name="UserName">按姓名查询时保留已消歧的名称。</param>
public sealed record OvertimeDay(DateOnly WorkDate, string UserId,
    IReadOnlyList<AttendanceRecord> Records, OvertimeInfo Overtime,
    string DayType, bool? OvertimeConfirmed, string InclusionReason,
    OvertimeDayClassification Classification, IReadOnlyList<OvertimeAnomaly> Anomalies, bool Success = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserName = null);

/// <summary>按日期类型计算时长；缺卡或分类未知时返回 null，不能用零代替未确认时长。</summary>
/// <param name="NormalWorkStart">工作日配置上班时间；非工作日／未知分类为 null。</param>
/// <param name="NormalWorkEnd">工作日计时起点；非工作日／未知分类为 null。</param>
/// <param name="ThresholdAt">工作日筛选门槛；非工作日／未知分类为 null。</param>
/// <param name="LastOffDutyAt">当天全部有效下班时间中的最晚一次，不依赖钉钉状态码或计划时间。</param>
/// <param name="OvertimeSeconds">已知的精确秒数，保留上游子秒精度；无法核算为 null。</param>
/// <param name="OvertimeHours">展示用小时数，四舍五入到两位小数，汇总不使用逐日舍入值相加。</param>
/// <param name="FirstOnDutyAt">最早有效上班卡；缺失时为 null。</param>
/// <param name="DurationBasis">work_end、first_on_to_last_off 或 unknown，不表示钉钉审批认定。</param>
/// <param name="DurationStatus">calculated 或 unavailable，区分可计算与缺失时长。</param>
public sealed record OvertimeInfo(DateTimeOffset? NormalWorkStart, DateTimeOffset? NormalWorkEnd,
    DateTimeOffset? ThresholdAt, DateTimeOffset? LastOffDutyAt, decimal? OvertimeSeconds, decimal? OvertimeHours,
    DateTimeOffset? FirstOnDutyAt, string DurationBasis, string DurationStatus);

/// <summary>来自原生休息天数列的分类事实；non_workday 包括该员工安排休息的节假日，不能进一步猜测法定类型。</summary>
/// <param name="Status">confirmed 或 unknown；unknown 时必须查看 Error。</param>
/// <param name="Source">dingtalk_report，明确不是按星期或原生加班时长推断。</param>
/// <param name="IsNonWorkday">true 为休息安排，false 为工作安排，null 为无法判定。</param>
/// <param name="Error">分类失败／不明确的安全错误，不含原始响应或员工 ID。</param>
public sealed record OvertimeDayClassification(string Status, string Source, bool? IsNonWorkday,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AttendanceError? Error = null);

/// <summary>下游可处理的异常；不替代原始打卡、不导致日期被过滤。</summary>
public sealed record OvertimeAnomaly(string Code, string Message);

/// <summary>仅汇总成功查询日期；先合计精确秒数，再换算展示小时，避免逐日舍入误差。</summary>
/// <param name="OvertimeDays">按工具规则确认的加班天数，包含缺卡但确有非工作日出勤的日期。</param>
/// <param name="TotalOvertimeSeconds">只合计已确认且可核算日期的精确秒数。</param>
/// <param name="TotalOvertimeHours">从总秒数换算的两位小时数，必须结合 DurationComplete 解释。</param>
/// <param name="ReturnedDays">实际返回日期数，包含未知分类候选。</param>
/// <param name="UnconfirmedDays">尚不能按工具规则确认加班的候选日期数。</param>
/// <param name="DurationUnconfirmedDays">已确认加班但无法核算时长的日期数。</param>
/// <param name="AnomalousDays">至少存在一项异常的返回日期数。</param>
/// <param name="DurationComplete">所有查询及已返回日期的时长均完整才为 true。</param>
public sealed record OvertimeSummary(int OvertimeDays, decimal TotalOvertimeSeconds, decimal TotalOvertimeHours,
    int ReturnedDays, int UnconfirmedDays, int DurationUnconfirmedDays, int AnomalousDays, bool DurationComplete);

/// <summary>单日查询失败，不代表缺卡或没有加班。</summary>
public sealed record OvertimeQueryError(DateOnly WorkDate, string UserId, AttendanceError Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserName = null);
