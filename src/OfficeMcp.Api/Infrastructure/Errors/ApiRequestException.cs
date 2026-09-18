namespace OfficeMcp.Api.Infrastructure.Errors;

/// <summary>可安全返回的应用请求错误，例如设备不可用、任务冲突或任务不存在。</summary>
public sealed class ApiRequestException(int statusCode, string code, string message, object? details = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    /// <summary>由业务明确构造的补充信息，例如重名员工候选；禁止传入上游原始错误。</summary>
    public object? Details { get; } = details;
}
