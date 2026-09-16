using System.Globalization;
using System.Text.Json.Serialization;
using OfficeMcp.Api.Infrastructure.Errors;

namespace OfficeMcp.Api.Infrastructure.DingTalk;

/// <summary>旧版钉钉业务 API 客户端，统一处理业务错误和一次令牌刷新。</summary>
public sealed class DingTalkClient(DingTalkTransport transport, DingTalkTokenProvider tokens)
{
    /// <summary>向固定的旧版 API 路径发送 JSON，并返回成功响应的 result。</summary>
    /// <typeparam name="T">业务结果模型。</typeparam>
    /// <param name="path">由服务代码指定的 topapi 相对路径，不从用户参数读取。</param>
    /// <param name="body">业务请求体。</param>
    /// <param name="cancellationToken">调用方取消标记。</param>
    public async Task<T> PostAsync<T>(string path, object body, CancellationToken cancellationToken) where T : class
    {
        if (!path.StartsWith("topapi/", StringComparison.Ordinal)
            || path.Contains("..", StringComparison.Ordinal) || path.IndexOfAny(['?', '#', '\\', ':']) >= 0)
            throw new ArgumentException("需要固定的 topapi 相对路径。", nameof(path));
        var token = await tokens.GetAsync(cancellationToken);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://oapi.dingtalk.com/{path}?access_token={Uri.EscapeDataString(token)}")
            {
                Content = JsonContent.Create(body)
            };
            var response = await transport.SendAsync<Envelope<T>>(request, cancellationToken);
            if (response.ErrorCode is null)
                throw new UpstreamException("dingtalk_invalid_response", "钉钉响应缺少业务状态码。");
            if (response.ErrorCode == 0)
                return response.Result ?? throw new UpstreamException("dingtalk_invalid_response", "钉钉响应缺少业务结果。");

            // 仅针对钉钉明确的令牌失效错误重试一次，权限错误和限流不重复调用。
            if (attempt == 0 && response.ErrorCode is 40001 or 40014 or 42001)
            {
                token = await tokens.GetAsync(cancellationToken, token);
                continue;
            }
            throw new UpstreamException("dingtalk_api_error", "钉钉拒绝了业务请求，请检查应用权限、用户可见范围和上游错误码。",
                response.ErrorCode.Value.ToString(CultureInfo.InvariantCulture));
        }
        throw new InvalidOperationException("钉钉请求未产生结果。");
    }

    private sealed class Envelope<T>
    {
        [JsonPropertyName("errcode")] public long? ErrorCode { get; init; }
        [JsonPropertyName("result")] public T? Result { get; init; }
    }
}
