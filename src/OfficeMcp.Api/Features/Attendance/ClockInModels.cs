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
    string WorkDate, string CheckType, string UserId, bool DeviceOnline, DateTimeOffset? CommandExpiresAt,
    string? DeviceOutcome, AttendanceRecord? Record, string? Message,
    string Source, ScheduledClockInReport? LocalExecution, DateTimeOffset CreatedAt,
    DateTimeOffset? VerificationDeadline, DateTimeOffset? FinishedAt, int VerificationAttempts,
    DateTimeOffset? LastVerifiedAt, string? VerificationError, string? VerificationRelation);

/// <summary>保留用于比较的钉钉记录标识，不向手机发送这些内部数据。</summary>
public sealed record ClockInSnapshot(long? RecordId, long? PlanId, DateTimeOffset? PlannedTime,
    DateTimeOffset? ActualTime, string? StatusCode);

/// <summary>任务核验元数据，持久化执行前基线，防止重启后将旧记录误判为新记录。</summary>
public sealed record ClockInMetadata(string WorkDate, string CheckType, string UserId, ClockInSnapshot? Baseline,
    ScheduledClockInReport? LocalExecution = null);

/// <summary>本地已执行事实；所有时间为 UTC Unix 毫秒，员工身份仅由服务端配置决定。</summary>
/// <param name="LocalRunId">动作前持久化的 UUID，同一次执行及重传始终使用原值。</param>
/// <param name="WorkDate">北京时间工作日，首版只支持当天上午的 OnDuty 计划。</param>
/// <param name="CheckType">首版固定为 OnDuty。</param>
/// <param name="ExecutedAtUnixMs">子任务实际进入动作流程的时间，不能用发送时间替代。</param>
/// <param name="Outcome">只表示设备动作结果，不能表示考勤成功。</param>
/// <param name="CompletedAtUnixMs">动作结束时间，中断时可以为空。</param>
/// <param name="ScreenOnAtUnixMs">确认亮屏时间，可为空。</param>
/// <param name="AppRequestedAtUnixMs">成功发送应用启动请求时间，可为空。</param>
/// <param name="ErrorCode">固定安全错误码，不接受原始异常或敏感内容。</param>
public sealed record ScheduledClockInReport(string LocalRunId, string WorkDate, string CheckType,
    long ExecutedAtUnixMs, string Outcome, long? CompletedAtUnixMs = null,
    long? ScreenOnAtUnixMs = null, long? AppRequestedAtUnixMs = null, string? ErrorCode = null);
