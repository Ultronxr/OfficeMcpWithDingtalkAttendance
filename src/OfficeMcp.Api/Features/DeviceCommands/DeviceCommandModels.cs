using System.Text.Json;
using System.Text.Json.Serialization;

namespace OfficeMcp.Api.Features.DeviceCommands;

/// <summary>通用设备命令配置；每个设备使用独立凭据。</summary>
public sealed class DeviceCommandOptions
{
    public bool Enabled { get; set; }
    public string StateDirectory { get; set; } = "data/device-commands";
    public Dictionary<string, DeviceRegistration> Devices { get; set; } = new();
}

/// <summary>一个可接收固定动作的已登记设备。</summary>
public sealed class DeviceRegistration
{
    public string Key { get; set; } = "";
}

/// <summary>持久化命令，Payload、Metadata 和 Result 由具体业务解释。</summary>
public sealed record DeviceCommandJob
{
    public const string RemoteCommand = "remote_command";
    public const string LocalSchedule = "local_schedule";
    public required string Id { get; init; }
    public required string RequestId { get; init; }
    public required string Fingerprint { get; init; }
    public required string DeviceId { get; init; }
    public required string Kind { get; init; }
    public required string Action { get; init; }
    // 旧任务没有 source 字段时，继续按远程命令解释，保持领取和核验兼容。
    public string Source { get; init; } = RemoteCommand;
    public string State { get; init; } = "queued";
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? ClaimedAt { get; init; }
    public DateTimeOffset? VerificationDeadline { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public int ExecutionTimeoutSeconds { get; init; } = 60;
    public int VerificationTimeoutSeconds { get; init; } = 120;
    public string? LeaseToken { get; init; }
    public string? DeviceOutcome { get; init; }
    public string? DeviceError { get; init; }
    public string? LastError { get; init; }
    public int VerificationAttempts { get; init; }
    public DateTimeOffset? LastVerifiedAt { get; init; }
    public string? VerificationError { get; init; }
    public string? VerificationRelation { get; init; }
    public required JsonElement Payload { get; init; }
    public required JsonElement Metadata { get; init; }
    public JsonElement? Result { get; init; }
    [JsonIgnore] public bool IsTerminal => State is "succeeded" or "already_completed" or "failed" or "expired" or "unconfirmed";
    [JsonIgnore] public bool RequiresDeviceAction => Source == RemoteCommand;
}

/// <summary>设备领取参数，长轮询最长等待 25 秒。</summary>
public sealed record DeviceLeaseRequest(int WaitSeconds = 25);

/// <summary>手机收到的固定动作；到期时间使用 UTC 毫秒，避免时区歧义。</summary>
public sealed record DeviceLease(string TaskId, string Action, string LeaseToken,
    long ServerTimeUnixMs, long ExpiresAtUnixMs, JsonElement Payload);

/// <summary>设备动作回执，只描述亮屏／启动请求，不能直接宣称考勤已成功。</summary>
public sealed record DeviceReport(string LeaseToken, string Outcome, string? ErrorCode = null);
