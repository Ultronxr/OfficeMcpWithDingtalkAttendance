using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace OfficeMcp.Api.Infrastructure.Errors;

/// <summary>将外部依赖失败和程序错误统一转换为 ProblemDetails。</summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>生成包含追踪号的错误响应；日志仅记录类别和安全错误码。</summary>
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = 499;
            return true;
        }
        var upstream = exception as UpstreamException;
        var requestError = exception as ApiRequestException;
        var status = upstream?.StatusCode ?? requestError?.StatusCode ?? 500;
        var code = upstream?.Code ?? requestError?.Code ?? "internal_error";
        // 不输出异常对象，防止 HttpClient 的异常文本包含带令牌的 URL。
        logger.LogWarning("请求失败：{Code}，上游错误码：{ProviderCode}，追踪号：{TraceId}",
            code, upstream?.ProviderCode, context.TraceIdentifier);
        context.Response.StatusCode = status;
        var problem = new ProblemDetails
        {
            Status = status,
            Title = requestError is not null ? "操作请求失败" : upstream is null ? "服务内部错误" : "钉钉请求失败",
            Detail = upstream?.Message ?? requestError?.Message ?? "请根据追踪号检查服务日志。",
            Extensions = { ["code"] = code, ["traceId"] = context.TraceIdentifier }
        };
        if (upstream?.ProviderCode is not null) problem.Extensions["providerCode"] = upstream.ProviderCode;
        await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json",
            cancellationToken: cancellationToken);
        return true;
    }
}
