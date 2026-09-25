using Microsoft.Extensions.Options;
using OfficeMcp.Api.Infrastructure.Time;

namespace OfficeMcp.Api.Features.Attendance;

/// <summary>在已有考勤范围查询之上按固定作息筛选加班日，不增加钉钉请求或设备动作。</summary>
public sealed class OvertimeService(AttendanceService attendance, IOptions<OvertimeOptions> options)
{

    /// <summary>一次复用考勤查询，分离失败日期，按工作日最晚下班卡计算加班并汇总。</summary>
    /// <param name="query">与原考勤工具共用的已校验日期、员工和明细参数。</param>
    /// <param name="cancellationToken">客户端取消标记，向原查询链路传递。</param>
    /// <returns>仅含加班日的考勤数据、统计和查询完整性信息。</returns>
    public async Task<OvertimeResponse> QueryAsync(AttendanceQueryParameters query, CancellationToken cancellationToken)
    {
        var configured = options.Value;
        var rule = new OvertimeRule(configured.WorkStartTime, configured.WorkEndTime, configured.ThresholdTime);
        var attendanceDays = await attendance.QueryAsync(query.StartDate, query.EndDate,
            query.UserId, query.UserName, query.FullDetail, cancellationToken);
        var overtimeDays = new List<OvertimeDay>();
        var errors = new List<OvertimeQueryError>();
        foreach (var day in attendanceDays)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!day.Success)
            {
                // 失败日期与确实没有下班卡的成功日期不同，不能当作确定未加班而静默丢弃。
                errors.Add(new(day.WorkDate, day.UserId,
                    day.Error ?? new AttendanceError("attendance_query_failed", "此日期考勤查询未成功。"), day.UserName));
                continue;
            }
            var lastOffDuty = day.Records.Where(record => record.CheckType == "OffDuty")
                .Select(record => record.ActualCheckTime).Max();
            if (lastOffDuty is null) continue;

            // 使用 workDate 构造配置时间，跨午夜不能只比较时分，也不借用上游计划时间。
            var thresholdAt = At(day.WorkDate, rule.ThresholdTime);
            if (lastOffDuty.Value < thresholdAt) continue;
            var normalEnd = At(day.WorkDate, rule.WorkEndTime);
            var seconds = (lastOffDuty.Value - normalEnd).Ticks / (decimal)TimeSpan.TicksPerSecond;
            overtimeDays.Add(new(day.WorkDate, day.UserId, day.Records,
                new OvertimeInfo(At(day.WorkDate, rule.WorkStartTime), normalEnd, thresholdAt,
                    lastOffDuty.Value, seconds, Hours(seconds)), UserName: day.UserName));
        }

        var totalSeconds = overtimeDays.Sum(day => day.Overtime.OvertimeSeconds);
        return new(query.StartDate, query.EndDate, rule, overtimeDays,
            new(overtimeDays.Count, totalSeconds, Hours(totalSeconds)), errors.Count == 0, errors);
    }

    /// <summary>将工作日和配置时刻组合为北京时间，不受运行服务器本地时区影响。</summary>
    private static DateTimeOffset At(DateOnly day, TimeOnly time) =>
        new(day.ToDateTime(time, DateTimeKind.Unspecified), OfficeTime.Offset);

    /// <summary>小时数只用于展示；精确秒数保留在响应中，支持调用方按自身精度使用。</summary>
    private static decimal Hours(decimal seconds) => Math.Round(seconds / 3600m, 2, MidpointRounding.AwayFromZero);
}
