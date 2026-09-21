namespace OfficeMcp.Api.Features.Attendance;

/// <summary>远程设置被动自动打卡；enabled 使用可空类型以拒绝漏传，重复操作必须复用请求 ID。</summary>
public sealed record AutomationRequest(string? RequestId, bool? Enabled, int WaitSeconds = 45);

/// <summary>手机确认已持久化并清理旧计划的版本；首次启动尚无本地状态时两个字段均为 null。</summary>
public sealed record AutomationSyncRequest(long? AppliedRevision, bool? AppliedEnabled);

/// <summary>下发给固定设备的期望策略，最近关闭版本用于离线后取消旧计划。</summary>
public sealed record AutomationPolicy(string DeviceId, long Revision, bool Enabled, long LastDisabledRevision);

/// <summary>开关状态和手机确认分别呈现；applied 表示手机已应用当前版本，不代表完成打卡。</summary>
public sealed record AutomationResponse(string DeviceId, bool DesiredEnabled, long Revision,
    long LastDisabledRevision, DateTimeOffset UpdatedAt, bool? AppliedEnabled, long? AppliedRevision,
    DateTimeOffset? AppliedAt, string SyncState, bool DeviceOnline, long? RequestRevision = null,
    bool RequestSuperseded = false);

/// <summary>请求去重记录永久保存；旧请求重传只查原版本，不覆盖后续操作。</summary>
public sealed record AutomationChange(bool Enabled, long Revision);

/// <summary>服务端原子持久化文档，与设备动作 Task 分开保存。</summary>
public sealed record AutomationDocument
{
    public required int Version { get; init; }
    public required string DeviceId { get; init; }
    public required bool Enabled { get; init; }
    public required long Revision { get; init; }
    public required long LastDisabledRevision { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public long? AppliedRevision { get; init; }
    public bool? AppliedEnabled { get; init; }
    public DateTimeOffset? AppliedAt { get; init; }
    public required Dictionary<string, AutomationChange> Requests { get; init; }
}
