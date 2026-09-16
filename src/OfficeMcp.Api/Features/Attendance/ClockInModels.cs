using System.ComponentModel.DataAnnotations;

namespace OfficeMcp.Api.Features.Attendance;

/// <summary>远程考勤流程的设备和时间限制。</summary>
public sealed class ClockInOptions
{
    public string DeviceId { get; set; } = "office-phone";
    public int CommandTtlSeconds { get; set; } = 120;
    public int ExecutionTimeoutSeconds { get; set; } = 60;
    public int VerificationTimeoutSeconds { get; set; } = 120;
    public int VerificationIntervalSeconds { get; set; } = 5;
}

/// <summary>创建现在打卡的请求；check_type 是核验目标，实际动作由钉钉自动打卡规则决定。</summary>
public sealed record ClockInRequest([property: Required] string RequestId, [property: Required] string CheckType,
    string? WorkDate = null, int WaitSeconds = 45);

/// <summary>远程任务的外部状态；只有 attendance_confirmed 表示已由官方 API 确认记录。</summary>
public sealed record ClockInTaskResponse(string TaskId, string State, bool IsTerminal, bool AttendanceConfirmed,
    string WorkDate, string CheckType, string UserId, bool DeviceOnline, DateTimeOffset CommandExpiresAt,
    string? DeviceOutcome, AttendanceRecord? Record, string? Message);

/// <summary>保留用于比较的钉钉记录标识，不向手机发送这些内部数据。</summary>
public sealed record ClockInSnapshot(long? RecordId, long? PlanId, DateTimeOffset? PlannedTime,
    DateTimeOffset? ActualTime, string? StatusCode);

/// <summary>任务核验元数据，持久化执行前基线，防止重启后将旧记录误判为新记录。</summary>
public sealed record ClockInMetadata(string WorkDate, string CheckType, string UserId, ClockInSnapshot? Baseline);
