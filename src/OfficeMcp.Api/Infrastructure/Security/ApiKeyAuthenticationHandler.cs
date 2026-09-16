using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OfficeMcp.Api.Infrastructure.Security;

/// <summary>服务间调用凭据配置，与钉钉应用凭据相互独立。</summary>
public sealed class ApiKeyOptions
{
    public string ApiKey { get; set; } = "";
}

/// <summary>校验固定请求头中的服务凭据，不从 URL 接收密钥。</summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    private readonly byte[] _expectedHash;

    /// <summary>初始化认证处理器，并散列配置中的密钥以进行恒定时间比较。</summary>
    public ApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, IOptions<ApiKeyOptions> apiKey)
        : base(options, logger, encoder)
    {
        _expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey.Value.ApiKey));
    }

    /// <summary>检查唯一的 API Key 请求头，成功后建立服务调用身份。</summary>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values))
            return Task.FromResult(AuthenticateResult.NoResult());
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]) || values[0]!.Length > 512)
            return Task.FromResult(AuthenticateResult.Fail("API Key 无效。"));
        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(values[0]!));
        if (!CryptographicOperations.FixedTimeEquals(_expectedHash, actualHash))
            return Task.FromResult(AuthenticateResult.Fail("API Key 无效。"));
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "office-gateway")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    /// <summary>返回明确的 401 JSON 错误，避免产生网页登录重定向。</summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = SchemeName;
        return Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = 401, Title = "未通过服务认证", Detail = "请提供有效的 X-Api-Key 请求头。"
        }, options: null, contentType: "application/problem+json", cancellationToken: Context.RequestAborted);
    }
}
