using System.Text.Json;
using Microsoft.Extensions.Options;
using OfficeMcp.Api.Infrastructure.Errors;

namespace OfficeMcp.Api.Features.DeviceCommands;

/// <summary>单实例命令存储：进程锁串行化领取，原子替换文件保证重启后仍可核验。</summary>
public sealed class DeviceCommandStore(IOptions<DeviceCommandOptions> options, TimeProvider clock) : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DeviceCommandJob> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private string? _directory;
    private FileStream? _instanceLock;

    /// <summary>加载持久化任务；损坏记录直接报错，不能悄悄忘记已经领取的动作。</summary>
    private void EnsureLoaded()
    {
        if (_directory is not null) return;
        var directory = Path.GetFullPath(options.Value.StateDirectory, AppContext.BaseDirectory);
        Directory.CreateDirectory(directory);
        // 同一状态目录只允许一个宿主持有，避免误启动第二个进程后重复领取。
        var instanceLock = new FileStream(Path.Combine(directory, ".instance.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            var loaded = Directory.EnumerateFiles(directory, "*.json").Select(path =>
                JsonSerializer.Deserialize<DeviceCommandJob>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("设备任务记录无效。")).ToArray();
            foreach (var job in loaded)
            {
                if (!Guid.TryParseExact(job.Id, "N", out _)) throw new InvalidDataException("设备任务 ID 无效。");
            }
            var byId = loaded.ToDictionary(job => job.Id, StringComparer.Ordinal);
            if (loaded.Select(job => job.RequestId).Distinct(StringComparer.Ordinal).Count() != loaded.Length)
                throw new InvalidDataException("设备任务的请求 ID 重复。");
            foreach (var entry in byId) _jobs.Add(entry.Key, entry.Value);
            _instanceLock = instanceLock;
            _directory = directory;
        }
        catch { instanceLock.Dispose(); throw; }
    }

    /// <summary>先落盘再更新内存；只有保存成功的领取状态才会交给手机。</summary>
    private void Save(DeviceCommandJob job)
    {
        var path = Path.Combine(_directory!, job.Id + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, job, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            _jobs[job.Id] = job;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>在存储锁内执行一个短事务，避免异步请求同时领取同一任务。</summary>
    private async Task<T> LockedAsync<T>(Func<T> operation, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { EnsureLoaded(); return operation(); }
        finally { _gate.Release(); }
    }

    /// <summary>按任务 ID 查询，不存在时返回 null。</summary>
    public Task<DeviceCommandJob?> GetAsync(string id, CancellationToken token) =>
        LockedAsync(() => _jobs.GetValueOrDefault(id), token);

    /// <summary>读取幂等请求对应的任务。</summary>
    public Task<DeviceCommandJob?> FindRequestAsync(string requestId, CancellationToken token) =>
        LockedAsync(() => _jobs.Values.FirstOrDefault(x => x.RequestId == requestId), token);

    /// <summary>创建命令；相同请求幂等，相同设备的其他未结束命令返回冲突。</summary>
    public Task<DeviceCommandJob> CreateAsync(DeviceCommandJob job, CancellationToken token) => LockedAsync(() =>
    {
        var prior = _jobs.Values.FirstOrDefault(x => x.RequestId == job.RequestId);
        if (prior is not null)
        {
            if (prior.Fingerprint != job.Fingerprint) throw new ApiRequestException(409, "request_id_conflict", "同一 request_id 不能用于不同操作。");
            return prior;
        }
        // 本地动作已经发生，必须允许登记事实；纯核验任务不占用远程动作名额。
        // 两条远程任务之间仍沿用原保护，手机动作继续由共用文件锁互斥。
        var active = _jobs.Values.FirstOrDefault(x => x.DeviceId == job.DeviceId && x.RequiresDeviceAction && !x.IsTerminal);
        if (job.RequiresDeviceAction && !job.IsTerminal && active is not null)
            throw new ApiRequestException(409, "device_busy", $"设备已有未结束任务，请查询任务 {active.Id}。");
        Save(job);
        return job;
    }, token);

    /// <summary>记录设备最近一次主动连接；不把服务重启前的心跳当成当前在线。</summary>
    public Task<DateTimeOffset> TouchAsync(string deviceId, CancellationToken token) => LockedAsync(() =>
    {
        var now = clock.GetUtcNow();
        _seen[deviceId] = now;
        return now;
    }, token);

    /// <summary>判断设备是否在最近 60 秒内连接过。</summary>
    public Task<bool> IsOnlineAsync(string deviceId, CancellationToken token) =>
        LockedAsync(() => _seen.TryGetValue(deviceId, out var seen) && clock.GetUtcNow() - seen <= TimeSpan.FromSeconds(60), token);

    /// <summary>仅领取尚未过期的排队任务；已经领取的任务永远不会因断线自动重发。</summary>
    public Task<DeviceCommandJob?> LeaseAsync(string deviceId, CancellationToken token) => LockedAsync(() =>
    {
        var now = clock.GetUtcNow();
        var job = _jobs.Values.Where(x => x.DeviceId == deviceId && x.RequiresDeviceAction
                && x.State == "queued" && x.ExpiresAt > now)
            .OrderBy(x => x.CreatedAt).FirstOrDefault();
        if (job is null) return null;
        job = job with
        {
            State = "claimed", ClaimedAt = now, LeaseToken = Guid.NewGuid().ToString("N"),
            VerificationDeadline = now.AddSeconds(job.ExecutionTimeoutSeconds + job.VerificationTimeoutSeconds)
        };
        Save(job);
        return job;
    }, token);

    /// <summary>原子更新最新状态，业务回调必须保留已经终结的任务。</summary>
    public Task<DeviceCommandJob> UpdateAsync(string id, Func<DeviceCommandJob, DeviceCommandJob> update,
        CancellationToken token) => LockedAsync(() =>
    {
        if (!_jobs.TryGetValue(id, out var current)) throw new ApiRequestException(404, "task_not_found", "任务不存在。");
        var next = update(current);
        if (next != current) Save(next);
        return next;
    }, token);

    /// <summary>获取一种业务的活跃任务快照，外部网络核验在锁外执行。</summary>
    public Task<DeviceCommandJob[]> ActiveAsync(string kind, CancellationToken token) =>
        LockedAsync(() => _jobs.Values.Where(x => x.Kind == kind && !x.IsTerminal).ToArray(), token);

    /// <summary>宿主退出时释放存储锁。</summary>
    public void Dispose()
    {
        _instanceLock?.Dispose();
        _gate.Dispose();
    }
}
