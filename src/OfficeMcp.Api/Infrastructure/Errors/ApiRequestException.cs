namespace OfficeMcp.Api.Infrastructure.Errors;

/// <summary>可安全返回的应用请求错误，例如设备不可用、任务冲突或任务不存在。</summary>
public sealed class ApiRequestException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
