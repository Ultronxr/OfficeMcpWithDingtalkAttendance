using System.Text.Json;
using Microsoft.Extensions.Options;
using OfficeMcp.Api.Features.DeviceCommands;
using OfficeMcp.Api.Infrastructure.Errors;

namespace OfficeMcp.Api.Features.Attendance;

/// <summary>固定设备开关存储：单实例文件锁、进程内串行事务和原子替换，独立于打卡 Task。</summary>
public sealed class AutomationStore(IOptions<DeviceCommandOptions> devices, IOptions<ClockInOptions> clockIn,
    TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileStream? _instanceLock;
    private string? _path;
    private AutomationDocument? _current;

    /// <summary>仅允许已启用的固定配对设备使用控制状态；损坏文件直接报错，不能恢复为开启。</summary>
    private void EnsureLoaded()
    {
        var deviceId = clockIn.Value.DeviceId;
        if (!devices.Value.Enabled || !devices.Value.Devices.ContainsKey(deviceId))
            throw new ApiRequestException(503, "remote_disabled", "远程设备功能未启用或固定设备未登记。");
        if (_current is not null) return;
        var directory = Path.Combine(Path.GetFullPath(devices.Value.StateDirectory, AppContext.BaseDirectory), "automation");
        Directory.CreateDirectory(directory);
        var instanceLock = new FileStream(Path.Combine(directory, ".instance.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            var path = Path.Combine(directory, "state.json");
            var exists = File.Exists(path);
            var document = exists
                ? JsonSerializer.Deserialize<AutomationDocument>(File.ReadAllText(path), DeviceCommandStore.JsonOptions)
                    ?? throw new InvalidDataException("自动打卡开关记录无效。")
                : new AutomationDocument { Version = 1, DeviceId = deviceId, Enabled = true, Revision = 0,
                    LastDisabledRevision = 0, UpdatedAt = clock.GetUtcNow(), Requests = new() };
            Validate(document, deviceId);
            _path = path;
            // 只为首次初始化落盘；读取旧记录不重写格式，避免升级或只读查询改动历史文件。
            if (exists) _current = document;
            else Save(document);
            _instanceLock = instanceLock;
        }
        catch { instanceLock.Dispose(); throw; }
    }

    /// <summary>校验持久化版本和完整请求序列，拒绝被截断或不一致的开关状态。</summary>
    private static void Validate(AutomationDocument value, string deviceId)
    {
        if (value.Version != 1 || value.DeviceId != deviceId || value.Revision < 0
            || value.Revision > 9007199254740991 || value.Requests is null
            || value.Requests.Count != value.Revision || value.UpdatedAt == default
            || value.Requests.Any(x => !Guid.TryParseExact(x.Key, "N", out _) || x.Value is null)
            || value.Requests.Values.Select(x => x.Revision).Order().Where((revision, index) => revision != index + 1L).Any())
            throw new InvalidDataException("自动打卡开关记录格式或请求序列无效。");
        var latest = value.Requests.Values.OrderByDescending(x => x.Revision).FirstOrDefault();
        var disabled = value.Requests.Values.Where(x => !x.Enabled).Select(x => x.Revision).DefaultIfEmpty(0).Max();
        if (value.Enabled != (latest?.Enabled ?? true) || value.LastDisabledRevision != disabled
            || (value.AppliedRevision is null) != (value.AppliedEnabled is null)
            || (value.AppliedRevision is null) != (value.AppliedAt is null)
            || (value.AppliedRevision is not null && !Matches(value, value.AppliedRevision.Value, value.AppliedEnabled!.Value)))
            throw new InvalidDataException("自动打卡开关记录内容不一致。");
    }

    /// <summary>先同步落盘再替换内存，写入失败保留原状态，临时文件不作为恢复来源。</summary>
    private void Save(AutomationDocument value)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, DeviceCommandStore.JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path!, overwrite: true);
            _current = value;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>所有状态读写经过同一短事务；网络请求和等待均在锁外进行。</summary>
    private async Task<T> LockedAsync<T>(Func<T> operation, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { EnsureLoaded(); return operation(); }
        finally { _gate.Release(); }
    }

    /// <summary>获取当前不可变状态快照；调用者不得修改其中的请求字典。</summary>
    public Task<AutomationDocument> GetAsync(CancellationToken token) => LockedAsync(() => _current!, token);

    /// <summary>设置显式开关并记录幂等版本；新请求即使值相同也等待手机确认本次设置。</summary>
    public Task<long> SetAsync(string requestId, bool enabled, CancellationToken token) => LockedAsync(() =>
    {
        var current = _current!;
        if (current.Requests.TryGetValue(requestId, out var prior))
        {
            if (prior.Enabled != enabled)
                throw new ApiRequestException(409, "request_id_conflict", "同一 request_id 不能用于不同开关设置。");
            return prior.Revision;
        }
        var revision = checked(current.Revision + 1);
        if (revision > 9007199254740991) throw new InvalidOperationException("自动打卡版本号超过设备支持范围。");
        var requests = new Dictionary<string, AutomationChange>(current.Requests, StringComparer.Ordinal)
        { [requestId] = new(enabled, revision) };
        Save(current with { Enabled = enabled, Revision = revision, UpdatedAt = clock.GetUtcNow(),
            LastDisabledRevision = enabled ? current.LastDisabledRevision : revision, Requests = requests });
        return revision;
    }, token);

    /// <summary>验证手机确认的是实际下发过的版本和值，不能伪造未来版本或把旧确认当成新开关生效。</summary>
    private static bool Matches(AutomationDocument document, long revision, bool enabled) =>
        revision == 0 ? enabled : revision > 0 && document.Requests.Values.Any(x => x.Revision == revision && x.Enabled == enabled);

    /// <summary>接受固定设备的持久化确认并返回最新策略；过时确认不覆盖已收到的更高版本。</summary>
    public Task<AutomationPolicy> SyncAsync(string deviceId, AutomationSyncRequest request, CancellationToken token) => LockedAsync(() =>
    {
        var current = _current!;
        if (current.DeviceId != deviceId)
            throw new ApiRequestException(403, "device_forbidden", "只能控制已绑定的考勤设备。");
        if ((request.AppliedRevision is null) != (request.AppliedEnabled is null)
            || (request.AppliedRevision is not null && !Matches(current, request.AppliedRevision.Value, request.AppliedEnabled!.Value)))
            throw new ApiRequestException(409, "automation_revision_conflict", "手机确认的开关版本或状态不匹配。");
        // null 表示手机本地状态丢失／首次安装，不能继续宣称旧版本已在手机生效。
        if (request.AppliedRevision is null && current.AppliedRevision is not null)
            Save(current with { AppliedRevision = null, AppliedEnabled = null, AppliedAt = null });
        else if (request.AppliedRevision is not null && (current.AppliedRevision is null || request.AppliedRevision > current.AppliedRevision))
            Save(current with { AppliedRevision = request.AppliedRevision, AppliedEnabled = request.AppliedEnabled, AppliedAt = clock.GetUtcNow() });
        return new AutomationPolicy(current.DeviceId, current.Revision, current.Enabled, current.LastDisabledRevision);
    }, token);

    /// <summary>服务退出后释放文件独占锁，下一实例才能恢复控制状态。</summary>
    public void Dispose()
    {
        _instanceLock?.Dispose();
        _gate.Dispose();
    }
}
