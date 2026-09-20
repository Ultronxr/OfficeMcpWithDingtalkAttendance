using System.Globalization;

namespace OfficeMcp.Api.Features.Attendance;

/// <summary>考勤与加班查询共用的已验证参数，保证员工选择、日期边界和明细模式一致。</summary>
public sealed record AttendanceQueryParameters(DateOnly StartDate, DateOnly EndDate,
    string? UserId, string? UserName, bool FullDetail)
{
    /// <summary>验证查询参数并收集全部可安全展示的错误；失败时不访问钉钉。</summary>
    /// <param name="context">用于检查单值参数和读取查询文本的请求上下文。</param>
    /// <param name="options">已有考勤查询范围限制。</param>
    /// <param name="errors">与原考勤接口一致的字段错误。</param>
    /// <returns>验证成功的参数，失败返回 null。</returns>
    public static AttendanceQueryParameters? Parse(HttpContext context, AttendanceOptions options,
        out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>();
        var start = ParseDate("start_date", context, errors);
        var end = ParseDate("end_date", context, errors);
        foreach (var name in new[] { "user_id", "user_name", "detail" })
            if (context.Request.Query[name].Count > 1) errors[name] = ["参数只能填写一个值。"];
        var userId = Normalize(context.Request.Query["user_id"].FirstOrDefault());
        var userName = Normalize(context.Request.Query["user_name"].FirstOrDefault());
        var detail = context.Request.Query["detail"].FirstOrDefault();
        if (userId is not null && userName is not null) errors["user_id"] = ["user_id 和 user_name 只能选择一个。"];
        if (userId?.Length > 256) errors["user_id"] = ["员工 ID 不能超过 256 个字符。"];
        if (userName?.Length > 100) errors["user_name"] = ["姓名不能超过 100 个字符。"];
        if (detail is not null and not "simple" and not "full") errors["detail"] = ["detail 仅支持 simple 或 full。"];
        if (errors.Count > 0) return null;
        if (start > end) errors["end_date"] = ["结束日期不能早于开始日期。"];
        else if (end.DayNumber - start.DayNumber + 1 > options.MaxQueryDays)
            errors["end_date"] = [$"单次查询最多 {options.MaxQueryDays} 天，包含开始和结束日期。"];
        return errors.Count > 0 ? null : new(start, end, userId, userName, detail == "full");
    }

    /// <summary>空白员工选择视为未填写，其他值只去除首尾空白。</summary>
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>只接受唯一且严格为 yyyy-MM-dd 的日期，保持原接口边界行为。</summary>
    private static DateOnly ParseDate(string name, HttpContext context, Dictionary<string, string[]> errors)
    {
        var values = context.Request.Query[name];
        if (values.Count == 1 && values[0]?.Length == 10
            && DateOnly.TryParseExact(values[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        errors[name] = ["请提供唯一有效的日期，格式为 yyyy-MM-dd，例如 2026-09-10。"];
        return default;
    }
}
