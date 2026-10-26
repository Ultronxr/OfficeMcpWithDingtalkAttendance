using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OfficeMcp.Api.Features.Attendance;
using OfficeMcp.Api.Features.DeviceCommands;

namespace OfficeMcp.Api.Tests;

/// <summary>合成设备通过内存 HTTP 完成探测；不连接真实手机、不访问钉钉、不触发实际动作。</summary>
public sealed class DeviceProbeTests
{
    private const string Endpoint = "/api/attendance/device-status";
    private const string Lease = "/api/devices/office-phone/commands/lease";
    private static readonly JsonSerializerOptions Json = DeviceCommandStore.JsonOptions;

    /// <summary>模拟新版手机领取纯通信挑战；等待正在启动的办公查询进入内存。</summary>
    private static async Task<DeviceProbeChallenge> Take(HttpClient phone)
    {
        using var response = await phone.PostAsJsonAsync(Lease, new { wait_seconds = 2, supports_probe = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var challenge = (await response.Content.ReadFromJsonAsync<DeviceProbeChallenge>(Json))!;
        Assert.Equal("connectivity_probe", challenge.Type);
        Assert.True(Guid.TryParseExact(challenge.ProbeId, "N", out _));
        return challenge;
    }

    /// <summary>模拟经过认证的手机回传标识，并读取是否接受。</summary>
    private static async Task<bool> Ack(HttpClient phone, string id, string deviceId = "office-phone")
    {
        using var response = await phone.PostAsJsonAsync($"/api/devices/{deviceId}/probes/{id}/ack", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetBoolean();
    }

    /// <summary>完整 HTTP 往返才确认在线；响应时间、缓存策略、任务及开关保持正确。</summary>
    [Fact]
    public async Task RoundTripConfirmsOnlineWithoutTaskOrPolicyMutation()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var api = factory.AuthenticatedClient();
        using var phone = factory.DeviceClient();
        var before = await api.GetFromJsonAsync<AutomationResponse>("/api/attendance/automation", Json);
        var query = api.GetAsync(Endpoint);
        var challenge = await Take(phone);
        Assert.False(query.IsCompleted);
        Assert.True(await Ack(phone, challenge.ProbeId));
        using var response = await query;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.True(root.GetProperty("online").GetBoolean());
        Assert.Equal("online", root.GetProperty("status").GetString());
        Assert.Equal("office-phone", root.GetProperty("device_id").GetString());
        Assert.Equal(10, root.GetProperty("wait_seconds").GetInt32());
        foreach (var name in new[] { "started_at", "checked_at" })
            Assert.Equal("2026-09-11T08:00:00+08:00", root.GetProperty(name).GetString());
        Assert.True(root.GetProperty("elapsed_ms").GetInt64() >= 0);
        Assert.False(root.TryGetProperty("time_zone", out _));
        var after = await api.GetFromJsonAsync<AutomationResponse>("/api/attendance/automation", Json);
        // 探测领取不改变开关响应；确认状态与实时在线结果分离。
        Assert.Equal(before, after);
        Assert.Empty(Directory.EnumerateFiles(factory.DeviceStateDirectory, "*.json"));
        Assert.Equal(0, factory.Handler.TokenCalls);
        Assert.Equal(0, factory.Handler.VerificationCalls);
    }

    /// <summary>手机已处于 25 秒长轮询时也能及时收到新挑战，无需等待旧轮询自然结束。</summary>
    [Fact]
    public async Task ProbeIsDeliveredInsideExistingLongPoll()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var phone = factory.DeviceClient();
        using var api = factory.AuthenticatedClient();
        var devices = factory.Services.GetRequiredService<DeviceCommandStore>();
        var lease = phone.PostAsJsonAsync(Lease, new { wait_seconds = 25, supports_probe = true });
        // 用原连接标记确认长轮询已经进入，而不是靠固定 sleep 猜测请求顺序。
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await devices.IsOnlineAsync("office-phone", deadline.Token)) await Task.Delay(10, deadline.Token);
        Assert.False(lease.IsCompleted);
        var query = api.GetFromJsonAsync<DeviceProbeResponse>(Endpoint + "?wait_seconds=5", Json);
        using var delivered = await lease.WaitAsync(TimeSpan.FromSeconds(3));
        var challenge = (await delivered.Content.ReadFromJsonAsync<DeviceProbeChallenge>(Json))!;
        Assert.Equal("connectivity_probe", challenge.Type);
        Assert.True(await Ack(phone, challenge.ProbeId));
        Assert.True((await query)!.Online);
    }

    /// <summary>旧心跳在线且已送出挑战仍不算成功，断在下行或应答链路时必须超时。</summary>
    [Fact]
    public async Task OldOnlineFlagAndDeliveryAloneNeverConfirmProbe()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var api = factory.AuthenticatedClient();
        using var phone = factory.DeviceClient();
        var devices = factory.Services.GetRequiredService<DeviceCommandStore>();
        await devices.TouchAsync("office-phone", default);
        var query = api.GetFromJsonAsync<DeviceProbeResponse>(Endpoint + "?wait_seconds=1", Json);
        var challenge = await Take(phone);
        var result = (await query)!;
        Assert.False(result.Online);
        Assert.Equal("probe_timeout", result.Status);
        Assert.True(await devices.IsOnlineAsync("office-phone", default));
        Assert.False(await Ack(phone, challenge.ProbeId));
        Assert.Null(factory.Services.GetRequiredService<DeviceProbeService>().Take("office-phone"));
    }

    /// <summary>从未连接的设备不会通过旧状态或无应答路径被误报为在线。</summary>
    [Fact]
    public async Task DisconnectedDeviceTimesOutAndLeavesNoChallenge()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var api = factory.AuthenticatedClient();
        var result = await api.GetFromJsonAsync<DeviceProbeResponse>(Endpoint + "?wait_seconds=1", Json);
        Assert.False(result!.Online);
        Assert.Equal("probe_timeout", result.Status);
        Assert.Null(factory.Services.GetRequiredService<DeviceProbeService>().Take("office-phone"));
    }

    /// <summary>旧版手机只收到 204，不会领取探测而走入旧动作执行路径。</summary>
    [Fact]
    public async Task LegacyLeaseCannotConsumeProbe()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var phone = factory.DeviceClient();
        var service = factory.Services.GetRequiredService<DeviceProbeService>();
        var query = service.QueryAsync(5, default);
        using var legacy = await phone.PostAsJsonAsync(Lease, new { wait_seconds = 0 });
        Assert.Equal(HttpStatusCode.NoContent, legacy.StatusCode);
        Assert.False(query.IsCompleted);
        var challenge = await Take(phone);
        Assert.True(await Ack(phone, challenge.ProbeId));
        Assert.True((await query).Online);
    }

    /// <summary>并发探测使用独立标识；其他设备、随机标识及重放均不能确认另一条查询。</summary>
    [Fact]
    public async Task ConcurrentProbesRequireTheirOwnDeviceAndChallenge()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var phone = factory.DeviceClient();
        using var other = factory.DeviceClient("other-phone");
        var service = factory.Services.GetRequiredService<DeviceProbeService>();
        var first = service.QueryAsync(5, default);
        var a = await Take(phone);
        var second = service.QueryAsync(5, default);
        var b = await Take(phone);
        Assert.NotEqual(a.ProbeId, b.ProbeId);
        Assert.False(await Ack(phone, Guid.NewGuid().ToString("N")));
        Assert.False(await Ack(other, a.ProbeId, "other-phone"));
        Assert.True(await Ack(phone, a.ProbeId));
        Assert.True((await first).Online);
        Assert.False(await Ack(phone, a.ProbeId));
        Assert.False(second.IsCompleted);
        Assert.True(await Ack(phone, b.ProbeId));
        Assert.True((await second).Online);
    }

    /// <summary>到期挑战不能用于下一次查询，重试必须完成新的往返。</summary>
    [Fact]
    public async Task ExpiredResponseCannotConfirmNewQuery()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var phone = factory.DeviceClient();
        var service = factory.Services.GetRequiredService<DeviceProbeService>();
        var first = service.QueryAsync(1, default);
        var old = await Take(phone);
        Assert.False((await first).Online);
        var second = service.QueryAsync(5, default);
        Assert.False(await Ack(phone, old.ProbeId));
        Assert.False(second.IsCompleted);
        var current = await Take(phone);
        Assert.True(await Ack(phone, current.ProbeId));
        Assert.True((await second).Online);
    }

    /// <summary>客户端取消释放未领取或已送达挑战，不遗留可被后续请求确认的待办。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCleansPendingChallenge(bool delivered)
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        var service = factory.Services.GetRequiredService<DeviceProbeService>();
        using var cancel = new CancellationTokenSource();
        var query = service.QueryAsync(30, cancel.Token);
        var challenge = delivered ? service.Take("office-phone") : null;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        Assert.Null(service.Take("office-phone"));
        if (challenge is not null) Assert.False(service.Acknowledge("office-phone", challenge.ProbeId));
    }

    /// <summary>办公与设备认证隔离，其他设备不能冒充固定手机应答。</summary>
    [Fact]
    public async Task AuthenticationBoundariesRemainEnforced()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var api = factory.AuthenticatedClient();
        using var anonymous = factory.CreateClient();
        using var phone = factory.DeviceClient();
        using var other = factory.DeviceClient("other-phone");
        var path = "/api/devices/office-phone/probes/" + Guid.NewGuid().ToString("N") + "/ack";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Endpoint)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync(Endpoint)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.PostAsJsonAsync(path, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(path, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(path, new { })).StatusCode);
    }

    /// <summary>非法等待值立即失败，不生成探测或业务任务。</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("31")]
    [InlineData("abc")]
    public async Task InvalidWaitIsRejected(string value)
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var api = factory.AuthenticatedClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync(Endpoint + "?wait_seconds=" + value)).StatusCode);
        Assert.Null(factory.Services.GetRequiredService<DeviceProbeService>().Take("office-phone"));
    }

    /// <summary>未启用或未登记目标设备属于配置错误，不能伪装成设备离线。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableConfigurationReturnsServiceError(bool enabled)
    {
        await using var factory = new OfficeApiFactory { EnableDevices = enabled };
        using var api = factory.AuthenticatedClient();
        if (enabled) factory.Services.GetRequiredService<IOptions<DeviceCommandOptions>>().Value.Devices.Remove("office-phone");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await api.GetAsync(Endpoint)).StatusCode);
    }

    /// <summary>探测不抢占已排队的打卡命令，原命令仅领取一次并保持原状态。</summary>
    [Fact]
    public async Task RealCommandKeepsPriorityOverProbe()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var phone = factory.DeviceClient();
        var store = factory.Services.GetRequiredService<DeviceCommandStore>();
        var id = Guid.NewGuid().ToString("N");
        var job = new DeviceCommandJob
        {
            Id = id, RequestId = id, Fingerprint = "synthetic", DeviceId = "office-phone",
            Kind = "attendance.clock_in", Action = "wake_dingtalk", CreatedAt = factory.Clock.GetUtcNow(),
            ExpiresAt = factory.Clock.GetUtcNow().AddMinutes(1),
            Payload = JsonSerializer.SerializeToElement(new { }), Metadata = JsonSerializer.SerializeToElement(new { })
        };
        await store.CreateAsync(job, default);
        var query = factory.Services.GetRequiredService<DeviceProbeService>().QueryAsync(5, default);
        using var response = await phone.PostAsJsonAsync(Lease, new { wait_seconds = 0, supports_probe = true });
        var command = (await response.Content.ReadFromJsonAsync<DeviceLease>(Json))!;
        Assert.Equal(id, command.TaskId);
        Assert.Equal("wake_dingtalk", command.Action);
        Assert.False(query.IsCompleted);
        var challenge = await Take(phone);
        Assert.True(await Ack(phone, challenge.ProbeId));
        Assert.True((await query).Online);
        Assert.Equal("claimed", (await store.GetAsync(id, default))!.State);
        Assert.Single(Directory.EnumerateFiles(factory.DeviceStateDirectory, "*.json"));
        Assert.Equal(0, factory.Handler.TokenCalls);
    }

    /// <summary>网关仅发现办公查询，隐藏手机挑战回执；参数范围和统一时间格式可被正确导入。</summary>
    [Fact]
    public async Task OpenApiExposesOnlyPublicProbeContract()
    {
        await using var factory = new OfficeApiFactory();
        using var api = factory.AuthenticatedClient();
        using var document = JsonDocument.Parse(await api.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");
        var operation = paths.GetProperty(Endpoint).GetProperty("get");
        Assert.Equal("attendance_device_status", operation.GetProperty("operationId").GetString());
        var wait = operation.GetProperty("parameters").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "wait_seconds");
        var schema = wait.GetProperty("schema");
        Assert.Equal(1, schema.GetProperty("minimum").GetInt32());
        Assert.Equal(30, schema.GetProperty("maximum").GetInt32());
        Assert.Equal(10, schema.GetProperty("default").GetInt32());
        Assert.DoesNotContain(paths.EnumerateObject(), x => x.Name.StartsWith("/api/devices/"));
        var result = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("DeviceProbeResponse").GetProperty("properties");
        Assert.Equal("boolean", result.GetProperty("online").GetProperty("type").GetString());
        Assert.Equal("date-time", result.GetProperty("checked_at").GetProperty("format").GetString());
    }
}
