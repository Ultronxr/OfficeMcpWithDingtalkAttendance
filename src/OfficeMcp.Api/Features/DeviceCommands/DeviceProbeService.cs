using System.Diagnostics;
using Microsoft.Extensions.Options;
using OfficeMcp.Api.Features.Attendance;
using OfficeMcp.Api.Infrastructure.Errors;

namespace OfficeMcp.Api.Features.DeviceCommands;

/// <summary>独立的内存在线探测：只有本次挑战的设备应答才能证明链路可用，不读写历史在线标记或业务任务。</summary>
public sealed class DeviceProbeService(IOptions<DeviceCommandOptions> devices,
    IOptions<ClockInOptions> attendance, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingProbe> _pending = new(StringComparer.Ordinal);

    /// <summary>为固定打卡设备创建一次探测，等待独立应答；超时返回未确认，取消传播并清理。</summary>
    /// <param name="waitSeconds">本次探测总等待期限，单位秒，范围 1～30。</param>
    /// <param name="token">办公查询请求的取消令牌。</param>
    public async Task<DeviceProbeResponse> QueryAsync(int waitSeconds, CancellationToken token)
    {
        if (waitSeconds is < 1 or > 30)
            throw new ApiRequestException(400, "invalid_wait_seconds", "探测等待时间为 1 至 30 秒。");
        var deviceId = attendance.Value.DeviceId;
        if (!devices.Value.Enabled || !devices.Value.Devices.ContainsKey(deviceId))
            throw new ApiRequestException(503, "device_probe_unavailable", "未启用或未登记打卡设备，无法探测。");
        token.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        var startedAt = clock.GetUtcNow();
        var pending = new PendingProbe(deviceId, waitSeconds);
        lock (_gate) _pending.Add(id, pending);
        try
        {
            // 使用单调计时限制本次查询；系统时间校正不能延长旧挑战的有效期。
            DateTimeOffset? acknowledgedAt = null;
            try { acknowledgedAt = await pending.Acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(waitSeconds), token); }
            catch (TimeoutException) { }
            token.ThrowIfCancellationRequested();
            return new(deviceId, acknowledgedAt.HasValue, acknowledgedAt.HasValue ? "online" : "probe_timeout",
                startedAt, acknowledgedAt ?? clock.GetUtcNow(), pending.Elapsed.ElapsedMilliseconds, waitSeconds);
        }
        finally
        {
            // 客户端离开或结果已返回后，晚到应答不能重新激活此探测。
            lock (_gate) _pending.Remove(id);
        }
    }

    /// <summary>仅为支持探测的设备领取一个未送达挑战；不触碰命令存储和动作名额。</summary>
    /// <param name="deviceId">已经通过设备认证并核对路由的设备 ID。</param>
    public DeviceProbeChallenge? Take(string deviceId)
    {
        lock (_gate)
        {
            foreach (var (id, pending) in _pending)
            {
                if (pending.DeviceId != deviceId || pending.Delivered || pending.Expired) continue;
                pending.Delivered = true;
                return new("connectivity_probe", id);
            }
            return null;
        }
    }

    /// <summary>核对设备、标识、实际送达状态与期限；错配、重放及晚到应答均不确认。</summary>
    /// <param name="deviceId">已经认证的设备 ID。</param>
    /// <param name="probeId">手机从领取响应读到的本次随机标识。</param>
    public bool Acknowledge(string deviceId, string probeId)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(probeId, out var pending) || pending.DeviceId != deviceId
                || !pending.Delivered || pending.Expired) return false;
            return pending.Acknowledged.TrySetResult(clock.GetUtcNow());
        }
    }

    /// <summary>单次查询的短期内存状态；异步继续在锁外运行，避免阻塞领取请求。</summary>
    private sealed class PendingProbe(string deviceId, int waitSeconds)
    {
        public string DeviceId { get; } = deviceId;
        public Stopwatch Elapsed { get; } = Stopwatch.StartNew();
        public bool Expired => Elapsed.Elapsed >= TimeSpan.FromSeconds(waitSeconds);
        public bool Delivered { get; set; }
        public TaskCompletionSource<DateTimeOffset> Acknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>纯通信挑战，与实际打卡命令形状分离，不含动作或持久化 Task。</summary>
public sealed record DeviceProbeChallenge(string Type, string ProbeId);

/// <summary>本次探测结果；超时仅表示未证实在线，耗时包含等待接收器领取的时间。</summary>
public sealed record DeviceProbeResponse(string DeviceId, bool Online, string Status,
    DateTimeOffset StartedAt, DateTimeOffset CheckedAt, long ElapsedMs, int WaitSeconds);
