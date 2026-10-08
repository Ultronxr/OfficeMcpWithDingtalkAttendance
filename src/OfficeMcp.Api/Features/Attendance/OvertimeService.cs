using System.Diagnostics;
using Microsoft.Extensions.Options;
using OfficeMcp.Api.Infrastructure.Time;

namespace OfficeMcp.Api.Features.Attendance;

/// <summary>复用考勤记录及原生休息安排；非工作日有记录即流出，异常与可计算时长分别表达。</summary>
public sealed class OvertimeService(AttendanceService attendance, OvertimeClassificationService classification,
    IOptions<OvertimeOptions> options, IOptions<AttendanceOptions> attendanceOptions)
{

    /// <summary>工作日按固定门槛、非工作日按实际记录纳入；未知分类保留候选并标记不完整。</summary>
    /// <param name="query">与原考勤工具共用的已校验日期、员工和明细参数。</param>
    /// <param name="cancellationToken">客户端取消标记，向原查询链路传递。</param>
    /// <returns>包括异常和待核验日期的记录，以及只覆盖已确认部分的汇总。</returns>
    public async Task<OvertimeResponse> QueryAsync(AttendanceQueryParameters query, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var configured = options.Value;
        var rule = new OvertimeRule(configured.WorkStartTime, configured.WorkEndTime, configured.ThresholdTime);
        var selected = await attendance.QueryWithEmployeeAsync(query.StartDate, query.EndDate,
            query.UserId, query.UserName, query.FullDetail, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = TimeSpan.FromSeconds(attendanceOptions.Value.QueryTimeoutSeconds) - elapsed.Elapsed;
        if (remaining <= TimeSpan.Zero) deadline.Cancel();
        else deadline.CancelAfter(remaining);
        // 复用已解析员工 ID；仅有记录的成功日期需要分类，无记录不凭计划制造出勤。
        var classifications = await classification.QueryAsync(selected.Days.Where(day => day.Success && day.Records.Count > 0)
            .Select(day => day.WorkDate).ToArray(), selected.UserId, cancellationToken, deadline.Token);
        var overtimeDays = new List<OvertimeDay>();
        var errors = new List<OvertimeQueryError>();
        foreach (var day in selected.Days)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!day.Success)
            {
                // 失败日期与确实没有下班卡的成功日期不同，不能当作确定未加班而静默丢弃。
                errors.Add(new(day.WorkDate, day.UserId,
                    day.Error ?? new AttendanceError("attendance_query_failed", "此日期考勤查询未成功。"), day.UserName));
                continue;
            }
            if (day.Records.Count == 0) continue;
            var facts = classifications[day.WorkDate];
            var firstOn = day.Records.Where(record => record.CheckType == "OnDuty").Select(record => record.ActualCheckTime).Min();
            var lastOff = day.Records.Where(record => record.CheckType == "OffDuty").Select(record => record.ActualCheckTime).Max();
            var threshold = At(day.WorkDate, rule.ThresholdTime);
            if (facts.IsNonWorkday == false && (lastOff is null || lastOff.Value < threshold)) continue;

            var anomalies = FindAnomalies(day.Records, firstOn, lastOff);
            decimal? seconds = null;
            var dayType = facts.IsNonWorkday switch { true => "non_workday", false => "workday", _ => "unknown" };
            var basis = facts.IsNonWorkday switch { true => "first_on_to_last_off", false => "work_end", _ => "unknown" };
            bool? confirmed = facts.IsNonWorkday.HasValue && day.Records.Any(record => record.ActualCheckTime.HasValue) ? true : null;
            if (facts.IsNonWorkday == false)
                seconds = Seconds(lastOff!.Value - At(day.WorkDate, rule.WorkEndTime));
            else if (facts.IsNonWorkday == true && firstOn.HasValue && lastOff.HasValue && lastOff >= firstOn)
                seconds = Seconds(lastOff.Value - firstOn.Value);
            if (facts.Error is not null)
            {
                errors.Add(new(day.WorkDate, day.UserId, facts.Error, day.UserName));
                anomalies.Add(new("classification_unavailable", "日期类型无法确认，记录保留供下游核验。"));
            }
            var info = new OvertimeInfo(
                facts.IsNonWorkday == false ? At(day.WorkDate, rule.WorkStartTime) : null,
                facts.IsNonWorkday == false ? At(day.WorkDate, rule.WorkEndTime) : null,
                facts.IsNonWorkday == false ? threshold : null,
                lastOff, seconds, seconds.HasValue ? Hours(seconds.Value) : null, firstOn, basis,
                seconds.HasValue ? "calculated" : "unavailable");
            overtimeDays.Add(new(day.WorkDate, day.UserId, day.Records, info, dayType, confirmed,
                facts.IsNonWorkday switch { true => "non_workday_record", false => "workday_threshold", _ => "classification_unavailable" },
                facts, anomalies, UserName: day.UserName));
        }

        // 缺卡日期仍计入已确认出勤天数；未知分类候选及未知时长不以零填补汇总。
        var confirmedDays = overtimeDays.Where(day => day.OvertimeConfirmed == true).ToArray();
        var totalSeconds = confirmedDays.Sum(day => day.Overtime.OvertimeSeconds ?? 0);
        var unknownDays = overtimeDays.Count(day => day.OvertimeConfirmed != true);
        var unknownDurations = confirmedDays.Count(day => !day.Overtime.OvertimeSeconds.HasValue);
        return new(query.StartDate, query.EndDate, rule, overtimeDays,
            new(confirmedDays.Length, totalSeconds, Hours(totalSeconds), overtimeDays.Count, unknownDays, unknownDurations,
                overtimeDays.Count(day => day.Anomalies.Count > 0), errors.Count == 0 && unknownDays == 0 && unknownDurations == 0),
            errors.Count == 0, errors);
    }

    /// <summary>收集缺卡、未知类型及逆序，不修改或删除原始记录，也不借用未知类型卡补齐上下班。</summary>
    private static List<OvertimeAnomaly> FindAnomalies(IReadOnlyList<AttendanceRecord> records,
        DateTimeOffset? firstOn, DateTimeOffset? lastOff)
    {
        var anomalies = new List<OvertimeAnomaly>();
        if (!firstOn.HasValue) anomalies.Add(new("missing_on_duty", "缺少有效上班打卡，已保留全部记录。"));
        if (!lastOff.HasValue) anomalies.Add(new("missing_off_duty", "缺少有效下班打卡，已保留全部记录。"));
        if (records.Any(record => record.CheckType is not ("OnDuty" or "OffDuty")))
            anomalies.Add(new("unknown_check_type", "存在未知打卡类型，不能用于补齐上下班卡。"));
        if (!records.Any(record => record.ActualCheckTime.HasValue))
            anomalies.Add(new("actual_time_unavailable", "记录中没有有效实际时间，不能确认出勤或计算时长。"));
        if (firstOn.HasValue && lastOff.HasValue && lastOff < firstOn)
            anomalies.Add(new("invalid_check_order", "最晚下班早于最早上班，无法按首末卡核算时长。"));
        return anomalies;
    }

    /// <summary>将工作日和配置时刻组合为北京时间，不受运行服务器本地时区影响。</summary>
    private static DateTimeOffset At(DateOnly day, TimeOnly time) =>
        new(day.ToDateTime(time, DateTimeKind.Unspecified), OfficeTime.Offset);

    /// <summary>使用内部原始精度计算秒数，不受秒级展示截断影响。</summary>
    private static decimal Seconds(TimeSpan elapsed) => elapsed.Ticks / (decimal)TimeSpan.TicksPerSecond;

    /// <summary>小时数只用于展示；精确秒数保留在响应中，支持调用方按自身精度使用。</summary>
    private static decimal Hours(decimal seconds) => Math.Round(seconds / 3600m, 2, MidpointRounding.AwayFromZero);
}
