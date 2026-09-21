using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OfficeMcp.Api.Features.Attendance;
using OfficeMcp.Api.Features.DeviceCommands;

namespace OfficeMcp.Api.Tests;

/// <summary>在内存 HTTP 宿主验证远程开关协议、重传、恢复和既有打卡链路，不接触真实手机。</summary>
public sealed class AutomationControlTests
{
    private const string Endpoint = "/api/attendance/automation";
    private const string Sync = "/api/devices/office-phone/attendance/automation/sync";
    private static readonly JsonSerializerOptions Json = DeviceCommandStore.JsonOptions;

    /// <summary>立即提交一个显式开关，不实际等待手机。</summary>
    private static Task<HttpResponseMessage> Set(HttpClient client, bool enabled, string? id = null, int wait = 0) =>
        client.PostAsJsonAsync(Endpoint, new AutomationRequest(id ?? Guid.NewGuid().ToString("D"), enabled, wait), Json);

    /// <summary>读取公开的控制结果，不依赖内部持久化字段。</summary>
    private static async Task<AutomationResponse> Read(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<AutomationResponse>(Json))!;

    /// <summary>手机初始未确认；关闭必须等待手机真正上报对应版本，不创建动作或调用钉钉。</summary>
    [Fact]
    public async Task DisableRequiresDeviceAcknowledgementAndNeverCreatesTask()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var client = factory.AuthenticatedClient();
        using var phone = factory.DeviceClient();
        var initial = await client.GetFromJsonAsync<AutomationResponse>(Endpoint, Json);
        Assert.True(initial!.DesiredEnabled);
        Assert.Null(initial.AppliedEnabled);
        Assert.Equal("pending", initial.SyncState);
        var response = await Set(client, false);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var requested = await Read(response);
        Assert.False(requested.DesiredEnabled);
        Assert.Equal(1, requested.Revision);
        Assert.Equal(1, requested.LastDisabledRevision);
        Assert.Equal("pending", requested.SyncState);
        // 返回策略还不算确认；必须等手机落盘后再上报。
        var sync = await phone.PostAsJsonAsync(Sync, new AutomationSyncRequest(null, null), Json);
        var policy = (await sync.Content.ReadFromJsonAsync<AutomationPolicy>(Json))!;
        Assert.False(policy.Enabled);
        Assert.Equal(1, policy.Revision);
        Assert.Null((await client.GetFromJsonAsync<AutomationResponse>(Endpoint, Json))!.AppliedRevision);
        Assert.Equal(HttpStatusCode.OK, (await phone.PostAsJsonAsync(Sync, new AutomationSyncRequest(1, false), Json)).StatusCode);
        var applied = await client.GetFromJsonAsync<AutomationResponse>(Endpoint, Json);
        Assert.Equal("applied", applied!.SyncState);
        Assert.Equal(false, applied.AppliedEnabled);
        Assert.Equal(factory.Clock.GetUtcNow(), applied.AppliedAt);
        Assert.True(applied.DeviceOnline);
        Assert.Equal(0, factory.Handler.TokenCalls);
        Assert.Equal(0, factory.Handler.VerificationCalls);
        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsJsonAsync("/api/devices/office-phone/commands/lease", new { wait_seconds = 0 })).StatusCode);
        Assert.Empty(Directory.EnumerateFiles(factory.DeviceStateDirectory, "*.json"));
    }

    /// <summary>重复请求不重置版本，旧关闭请求重试不能覆盖后续开启，UUID 值冲突必须拒绝。</summary>
    [Fact]
    public async Task IdempotentRetriesNeverUndoNewerRequests()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var client = factory.AuthenticatedClient();
        using var phone = factory.DeviceClient();
        var id = Guid.NewGuid().ToString("D");
        var first = await Read(await Set(client, false, id));
        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        var repeated = await Read(await Set(client, false, id.ToUpperInvariant()));
        Assert.Equal(first.Revision, repeated.Revision);
        Assert.Equal(first.UpdatedAt, repeated.UpdatedAt);
        await phone.PostAsJsonAsync(Sync, new AutomationSyncRequest(1, false), Json);
        Assert.Equal(HttpStatusCode.OK, (await Set(client, false, id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Set(client, true, id)).StatusCode);
        var enabled = await Read(await Set(client, true));
        Assert.True(enabled.DesiredEnabled);
        Assert.Equal(2, enabled.Revision);
        Assert.Equal(1, enabled.LastDisabledRevision);
        Assert.Equal("pending", enabled.SyncState);
        var old = await Read(await Set(client, false, id));
        Assert.Equal(2, old.Revision);
        Assert.Equal(1, old.RequestRevision);
        Assert.True(old.RequestSuperseded);
        Assert.True(old.DesiredEnabled);
    }

    /// <summary>晚到确认既不确认未来设置，也不覆盖已经确认的更高版本；设备重装需重新确认。</summary>
    [Fact]
    public async Task StaleAcknowledgementsAndDeviceResetAreExplicit()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var client = factory.AuthenticatedClient();
        using var phone = factory.DeviceClient();
        await Set(client, false);
        await Set(client, true);
        await phone.PostAsJsonAsync(Sync, new AutomationSyncRequest(1, false), Json);
        var pending = await client.GetFromJsonAsync<AutomationResponse>(Endpoint, Json);
        Assert.Equal("pending", pending!.SyncState);
        Assert.Equal(1, pending.AppliedRevision);
        await phone.PostAsJsonAsync(Sync, new AutomationSyncRequest(2, true), Json);
        await phone.PostAsJsonAsync(Sync, new AutomationSyncRequest(1, false), Json);
        Assert.Equal(2, (await client.GetFromJsonAsync<AutomationResponse>(Endpoint, Json))!.AppliedRevision);
        await phone.PostAsJsonAsync(Sync, new AutomationSyncRequest(null, null), Json);
        var reset = await client.GetFromJsonAsync<AutomationResponse>(Endpoint, Json);
        Assert.Null(reset!.AppliedRevision);
        Assert.Equal("pending", reset.SyncState);
        Assert.Equal(2, reset.Revision);
    }

    /// <summary>未知／不匹配的确认不能使接口显示手机已关闭。</summary>
    [Theory]
    [InlineData(-1L, false)]
    [InlineData(0L, false)]
    [InlineData(1L, true)]
    [InlineData(2L, false)]
    [InlineData(null, false)]
    [InlineData(1L, null)]
    public async Task InvalidAcknowledgementsAreRejected(long? revision, bool? enabled)
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var client = factory.AuthenticatedClient();
        using var phone = factory.DeviceClient();
        await Set(client, false);
        Assert.Equal(HttpStatusCode.Conflict,
            (await phone.PostAsJsonAsync(Sync, new AutomationSyncRequest(revision, enabled), Json)).StatusCode);
        Assert.Equal("pending", (await client.GetFromJsonAsync<AutomationResponse>(Endpoint, Json))!.SyncState);
    }

    /// <summary>显式 enabled、规范 UUID 和等待上限均由 API 验证，不把缺省布尔值解释成关闭。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"request_id\":\"b45b6c3b-bbcb-42cb-994a-e2ecf64391e6\"}")]
    [InlineData("{\"request_id\":\"bad\",\"enabled\":false}")]
    [InlineData("{\"request_id\":\"b45b6c3b-bbcb-42cb-994a-e2ecf64391e6\",\"enabled\":null}")]
    [InlineData("{\"request_id\":\"b45b6c3b-bbcb-42cb-994a-e2ecf64391e6\",\"enabled\":\"false\"}")]
    [InlineData("{\"request_id\":\"b45b6c3b-bbcb-42cb-994a-e2ecf64391e6\",\"enabled\":false,\"wait_seconds\":-1}")]
    [InlineData("{\"request_id\":\"b45b6c3b-bbcb-42cb-994a-e2ecf64391e6\",\"enabled\":false,\"wait_seconds\":61}")]
    public async Task InvalidSetRequestsDoNotChangeState(string json)
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var client = factory.AuthenticatedClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync(Endpoint, new StringContent(json, Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<AutomationResponse>(Endpoint, Json))!.Revision);
    }

    /// <summary>办公与设备认证隔离；配对的其他设备不能查询或确认固定考勤设备的开关。</summary>
    [Fact]
    public async Task AuthenticationAndDeviceBindingAreEnforced()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var anonymous = factory.CreateClient();
        using var client = factory.AuthenticatedClient();
        using var phone = factory.DeviceClient();
        using var other = factory.DeviceClient("other-phone");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Endpoint)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Set(anonymous, false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync(Endpoint)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Sync, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(Sync, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(
            "/api/devices/other-phone/attendance/automation/sync", new { })).StatusCode);
        await using var disabled = new OfficeApiFactory();
        using var disabledClient = disabled.AuthenticatedClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await disabledClient.GetAsync(Endpoint)).StatusCode);
    }

    /// <summary>设置请求等待期间手机完成确认会返回 200；超时仅留下已保存设置，不假装生效。</summary>
    [Fact]
    public async Task WaitingRequestCompletesWhenPhoneAppliesVersion()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var client = factory.AuthenticatedClient();
        using var phone = factory.DeviceClient();
        var pending = Set(client, false, wait: 3);
        // 有界等待 HTTP 设置进入存储，再让虚拟手机确认，避免依赖线程调度先后。
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if ((await client.GetFromJsonAsync<AutomationResponse>(Endpoint, Json))!.Revision == 1) break;
            await Task.Delay(10);
        }
        Assert.Equal(HttpStatusCode.OK, (await phone.PostAsJsonAsync(Sync, new AutomationSyncRequest(1, false), Json)).StatusCode);
        var response = await pending;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("applied", (await Read(response)).SyncState);
    }

    /// <summary>并发同请求只产生一个版本，不同请求的关闭／开启序列保持可恢复的连续版本。</summary>
    [Fact]
    public async Task ConcurrentRequestsAreSerializedAndDeduplicated()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var client = factory.AuthenticatedClient();
        var id = Guid.NewGuid().ToString("D");
        var repeated = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Set(client, false, id)));
        foreach (var response in repeated) Assert.Equal(1, (await Read(response)).Revision);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Set(client, i % 2 == 0)));
        var state = await client.GetFromJsonAsync<AutomationResponse>(Endpoint, Json);
        Assert.Equal(9, state!.Revision);
        Assert.Equal(9, (await factory.Services.GetRequiredService<AutomationStore>().GetAsync(default)).Requests.Count);
    }

    /// <summary>关闭被动任务后仍可创建主动打卡，已经发生的本地动作仍能补报核验。</summary>
    [Fact]
    public async Task DisabledAutomationDoesNotBlockRemoteActionsOrLocalVerification()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var client = factory.AuthenticatedClient();
        using var phone = factory.DeviceClient();
        await Set(client, false);
        await phone.PostAsJsonAsync(Sync, new AutomationSyncRequest(1, false), Json);
        var remote = await client.PostAsJsonAsync("/api/attendance/clock-in", new {
            request_id = Guid.NewGuid().ToString("D"), check_type = "OnDuty", wait_seconds = 0 });
        Assert.Equal(HttpStatusCode.Accepted, remote.StatusCode);
        var now = factory.Clock.GetUtcNow();
        var local = await phone.PostAsJsonAsync("/api/devices/office-phone/attendance/executions",
            new ScheduledClockInReport(Guid.NewGuid().ToString("N"), now.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd"),
                "OnDuty", now.AddSeconds(-20).ToUnixTimeMilliseconds(), "uncertain"), Json);
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        Assert.Equal("verifying", (await local.Content.ReadFromJsonAsync<ClockInTaskResponse>(Json))!.State);
        var lease = await phone.PostAsJsonAsync("/api/devices/office-phone/commands/lease", new { wait_seconds = 0 });
        Assert.Equal(HttpStatusCode.OK, lease.StatusCode);
        Assert.Equal("wake_dingtalk", (await lease.Content.ReadFromJsonAsync<DeviceLease>(Json))!.Action);
    }

    /// <summary>重启保留禁用状态及请求去重；单实例锁阻止同时打开同一份控制存储。</summary>
    [Fact]
    public async Task StoreSurvivesRestartAndRejectsCorruption()
    {
        var directory = Path.Combine(Path.GetTempPath(), "office-mcp-control-tests", Guid.NewGuid().ToString("N"));
        var options = Options.Create(new DeviceCommandOptions { Enabled = true, StateDirectory = directory,
            Devices = new() { ["office-phone"] = new() { Key = OfficeApiFactory.DeviceKey } } });
        var device = Options.Create(new ClockInOptions { DeviceId = "office-phone" });
        var clock = new TestClock();
        var id = Guid.NewGuid().ToString("N");
        try
        {
            using (var first = new AutomationStore(options, device, clock))
            {
                await first.SetAsync(id, false, default);
                await first.SyncAsync("office-phone", new(1, false), default);
                using var collision = new AutomationStore(options, device, clock);
                await Assert.ThrowsAsync<IOException>(() => collision.GetAsync(default));
            }
            using (var restored = new AutomationStore(options, device, clock))
            {
                var state = await restored.GetAsync(default);
                Assert.False(state.Enabled);
                Assert.Equal(1, state.AppliedRevision);
                Assert.Equal(1, await restored.SetAsync(id, false, default));
                Assert.Equal(1, state.LastDisabledRevision);
            }
            var file = Path.Combine(directory, "automation", "state.json");
            // 存在但损坏的状态不能静默恢复默认开启。
            await File.WriteAllTextAsync(file, "{}");
            using var corrupted = new AutomationStore(options, device, clock);
            await Assert.ThrowsAsync<JsonException>(() => corrupted.GetAsync(default));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    /// <summary>OpenAPI 暴露两个办公工具和严格请求模式，设备同步及凭据均不出现在工具文档。</summary>
    [Fact]
    public async Task OpenApiDocumentsControlButExcludesDeviceSync()
    {
        await using var factory = new OfficeApiFactory();
        using var client = factory.AuthenticatedClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var root = document.RootElement;
        var paths = root.GetProperty("paths");
        var route = paths.GetProperty(Endpoint);
        Assert.Equal("attendance_automation_set", route.GetProperty("post").GetProperty("operationId").GetString());
        Assert.Equal("attendance_automation_status", route.GetProperty("get").GetProperty("operationId").GetString());
        Assert.DoesNotContain(paths.EnumerateObject(), x => x.Name.Contains("/devices/"));
        var schema = root.GetProperty("components").GetProperty("schemas").GetProperty("AutomationRequest");
        Assert.Equal(new[] { "enabled", "request_id" }, schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).Order());
        Assert.Equal(45, schema.GetProperty("properties").GetProperty("wait_seconds").GetProperty("default").GetInt32());
        Assert.False(schema.GetProperty("properties").GetProperty("enabled").TryGetProperty("default", out _));
        Assert.True(route.GetProperty("post").GetProperty("responses").TryGetProperty("202", out _));
    }
}
