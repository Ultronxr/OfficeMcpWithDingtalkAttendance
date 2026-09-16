using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OfficeMcp.Api.Features.DeviceCommands;

/// <summary>手机专用认证；设备凭据只能用于领取和回报本设备命令。</summary>
public sealed class DeviceAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DeviceKey";
    public const string PolicyName = "DeviceOnly";
    private readonly DeviceCommandOptions _devices;

    /// <summary>初始化设备认证处理器，默认办公 API 仍使用原来的独立认证。</summary>
    public DeviceAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, IOptions<DeviceCommandOptions> devices) : base(options, logger, encoder)
    {
        _devices = devices.Value;
    }

    /// <summary>以恒定时间比较已登记设备密钥，并建立设备身份。</summary>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var id = Request.Headers["X-Device-Id"];
        var key = Request.Headers["X-Device-Key"];
        if (!_devices.Enabled || id.Count != 1 || key.Count != 1 || key[0]?.Length is not (>= 32 and <= 512)
            || !_devices.Devices.TryGetValue(id[0]!, out var device))
            return Task.FromResult(AuthenticateResult.Fail("设备凭据无效。"));
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(key[0]!));
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(device.Key));
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            return Task.FromResult(AuthenticateResult.Fail("设备凭据无效。"));
        var identity = new ClaimsIdentity([new Claim("device_id", id[0]!)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    /// <summary>设备认证失败返回 JSON，不输出凭据或重定向页面。</summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.WWWAuthenticate = SchemeName;
        return Response.WriteAsJsonAsync(new ProblemDetails { Status = 401, Title = "设备认证失败" },
            options: null, contentType: "application/problem+json", cancellationToken: Context.RequestAborted);
    }
}
