namespace OfficeMcp.Api.Infrastructure.Errors;

/// <summary>外部依赖失败；只携带可安全返回的错误信息，不保存响应正文和凭据。</summary>
public sealed class UpstreamException : Exception
{
    public string Code { get; }
    public string? ProviderCode { get; }
    public int StatusCode { get; }

    /// <summary>创建包含稳定业务错误码和 HTTP 状态的外部依赖异常。</summary>
    public UpstreamException(string code, string message, string? providerCode = null, int statusCode = 502)
        : base(message)
    {
        Code = code;
        ProviderCode = providerCode;
        StatusCode = statusCode;
    }
}
