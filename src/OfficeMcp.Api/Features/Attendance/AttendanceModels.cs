using System.Text.Json.Serialization;
using OfficeMcp.Api.Infrastructure.DingTalk;

namespace OfficeMcp.Api.Features.Attendance;

/// <summary>考勤模块独立配置；用户不能通过接口参数覆盖固定用户。</summary>
public sealed class AttendanceOptions
{
    public string UserId { get; set; } = "";
    public int MaxQueryDays { get; set; } = 31;
    public int MaxParallelDays { get; set; } = 3;
    public int QueryTimeoutSeconds { get; set; } = 120;
}

/// <summary>单个工作日的查询结果；仅 success=true 时，空 records 表示未返回考勤结果。</summary>
/// <param name="WorkDate">考勤工作日，跨天的下班记录仍属于此工作日。</param>
/// <param name="UserId">脱敏后的固定钉钉用户 ID，保留首尾各三位，中间以星号替代。</param>
/// <param name="TimeZone">时间解释所使用的时区。</param>
/// <param name="Records">当天全部上下班记录，按计划时间排序。</param>
/// <param name="Success">当日查询是否成功；成功且没有记录才代表无考勤结果。</param>
/// <param name="Error">当日查询失败的安全错误信息；成功时不输出此字段。</param>
public sealed record AttendanceResponse(DateOnly WorkDate, string UserId, string TimeZone,
    IReadOnlyList<AttendanceRecord> Records, bool Success = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AttendanceError? Error = null);

/// <summary>单个工作日的失败信息，不包含令牌、上游原始响应或完整用户 ID。</summary>
/// <param name="Code">稳定的错误码。</param>
/// <param name="Message">可直接展示的错误说明。</param>
/// <param name="ProviderCode">钉钉返回的错误码，便于定位权限等问题。</param>
public sealed record AttendanceError(string Code, string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProviderCode = null);

/// <summary>一条整理后的上下班记录；状态直接取自钉钉，不做二次迟到判定。</summary>
/// <param name="CheckType">原始上下班类型。</param>
/// <param name="CheckTypeName">中文上下班类型。</param>
/// <param name="PlannedCheckTime">计划打卡时间，带 +08:00 时区。</param>
/// <param name="ActualCheckTime">实际打卡时间；没有打卡时间时为 null。</param>
/// <param name="StatusCode">钉钉原始考勤状态码。</param>
/// <param name="Status">中文状态，未识别状态保留原始码供调用者判断。</param>
public sealed record AttendanceRecord(string? CheckType, string CheckTypeName,
    DateTimeOffset? PlannedCheckTime, DateTimeOffset? ActualCheckTime, string? StatusCode, string Status);

/// <summary>只读取业务需要的钉钉字段，不暴露地址、设备和审批原文。</summary>
internal sealed class DingTalkAttendanceResult
{
    [JsonPropertyName("userid")] public string? UserId { get; init; }
    [JsonPropertyName("attendance_result_list")] public List<DingTalkAttendanceRecord>? Records { get; init; }
}

/// <summary>钉钉打卡结果模型，与外部 HTTP 响应模型隔离。</summary>
internal sealed class DingTalkAttendanceRecord
{
    [JsonPropertyName("record_id")] public long? RecordId { get; init; }
    [JsonPropertyName("plan_id")] public long? PlanId { get; init; }
    [JsonPropertyName("check_type")] public string? CheckType { get; init; }
    [JsonPropertyName("time_result")] public string? TimeResult { get; init; }
    [JsonPropertyName("plan_check_time"), JsonConverter(typeof(DingTalkDateTimeConverter))]
    public DateTimeOffset? PlannedCheckTime { get; init; }
    [JsonPropertyName("user_check_time"), JsonConverter(typeof(DingTalkDateTimeConverter))]
    public DateTimeOffset? ActualCheckTime { get; init; }
}
