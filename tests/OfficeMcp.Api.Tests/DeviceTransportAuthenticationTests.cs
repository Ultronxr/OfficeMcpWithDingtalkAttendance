using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace OfficeMcp.Api.Tests;

/// <summary>覆盖将由公网代理暴露的六类设备接口；仅使用合成凭据，不改变现有认证实现。</summary>
public sealed class DeviceTransportAuthenticationTests
{
    private const string Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>为六类原设备路径提供合法绑定的请求体，认证失败不能被模型错误遮蔽。</summary>
    private static IEnumerable<(HttpMethod Method, string Path, object? Body)> Routes()
    {
        yield return (HttpMethod.Post, "/commands/lease", new { wait_seconds = 0 });
        yield return (HttpMethod.Post, "/commands/" + Id + "/report", new { lease_token = Id, outcome = "failed" });
        yield return (HttpMethod.Post, "/probes/" + Id + "/ack", null);
        yield return (HttpMethod.Post, "/attendance/executions", new
        {
            local_run_id = Id, work_date = "2026-09-11", check_type = "OnDuty",
            executed_at_unix_ms = 1789084800000L, outcome = "uncertain"
        });
        yield return (HttpMethod.Post, "/attendance/automation/sync", new { applied_revision = (long?)null, applied_enabled = (bool?)null });
        yield return (HttpMethod.Get, "/attendance/tasks/" + Id, null);
    }

    /// <summary>不携带、错误或多值设备密钥以及其他办公身份均不能通过设备认证。</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("office")]
    [InlineData("gateway")]
    [InlineData("wrong_key")]
    [InlineData("duplicate_id")]
    [InlineData("duplicate_key")]
    public async Task EveryPublicDeviceOperationRequiresItsOwnCredentials(string credential)
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var client = factory.CreateClient();
        foreach (var (method, path, body) in Routes())
        {
            using var request = new HttpRequestMessage(method, "/api/devices/office-phone" + path);
            if (body is not null) request.Content = JsonContent.Create(body);
            if (credential == "office") request.Headers.Add("X-Api-Key", OfficeApiFactory.ApiKey);
            if (credential == "gateway") request.Headers.Add("Authorization", "Bearer synthetic-gateway-token");
            if (credential is "wrong_key" or "duplicate_id" or "duplicate_key")
            {
                request.Headers.Add("X-Device-Id", credential == "duplicate_id"
                    ? new[] { "office-phone", "office-phone" } : new[] { "office-phone" });
                request.Headers.Add("X-Device-Key", credential == "duplicate_key"
                    ? new[] { OfficeApiFactory.DeviceKey, OfficeApiFactory.DeviceKey }
                    : new[] { credential == "wrong_key" ? new string('x', 40) : OfficeApiFactory.DeviceKey });
            }
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(OfficeApiFactory.DeviceKey, text);
            Assert.DoesNotContain(OfficeApiFactory.ApiKey, text);
        }
        Assert.Equal(0, factory.Handler.TokenCalls);
    }

    /// <summary>有效的另一设备密钥也不能领取、回报或读取固定打卡手机的任何资源。</summary>
    [Fact]
    public async Task AuthenticatedDeviceCannotUseAnotherDeviceRoute()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var other = factory.DeviceClient("other-phone");
        foreach (var (method, path, body) in Routes())
        {
            using var request = new HttpRequestMessage(method, "/api/devices/office-phone" + path);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await other.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        Assert.Equal(0, factory.Handler.TokenCalls);
    }

    /// <summary>原设备身份保持可用，领取和开关同步无需 OAuth 或办公 API Key。</summary>
    [Fact]
    public async Task OriginalDeviceProtocolDoesNotAcquireNewAuthenticationRequirements()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var phone = factory.DeviceClient();
        using var lease = await phone.PostAsJsonAsync("/api/devices/office-phone/commands/lease", new { wait_seconds = 0 });
        Assert.Equal(HttpStatusCode.NoContent, lease.StatusCode);
        using var sync = await phone.PostAsJsonAsync("/api/devices/office-phone/attendance/automation/sync",
            new { applied_revision = (long?)null, applied_enabled = (bool?)null });
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        using var policy = JsonDocument.Parse(await sync.Content.ReadAsStringAsync());
        Assert.Equal("office-phone", policy.RootElement.GetProperty("device_id").GetString());
        Assert.Equal(0, factory.Handler.TokenCalls);
    }
}
