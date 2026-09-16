using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OfficeMcp.Api.Infrastructure.Errors;

namespace OfficeMcp.Api.Infrastructure.DingTalk;

/// <summary>共享的钉钉 HTTP 传输，约束响应大小、超时和错误信息。</summary>
public sealed class DingTalkTransport(IHttpClientFactory factory, IOptions<DingTalkOptions> options)
{
    public const string ClientName = "DingTalk";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>发送一个请求并反序列化结果；调用方负责释放请求。</summary>
    /// <typeparam name="T">外部响应模型。</typeparam>
    /// <param name="request">发送到钉钉固定域名的请求。</param>
    /// <param name="cancellationToken">上层请求取消标记。</param>
    public async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var client = factory.CreateClient(ClientName);
            client.Timeout = TimeSpan.FromSeconds(options.Value.TimeoutSeconds);
            client.MaxResponseContentBufferSize = 2 * 1024 * 1024;
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // 仅提取短错误码；不返回可能包含凭据或个人数据的外部错误正文。
                throw new UpstreamException("dingtalk_http_error", "钉钉服务返回 HTTP 错误。",
                    ReadSafeCode(body) ?? ((int)response.StatusCode).ToString());
            }
            return JsonSerializer.Deserialize<T>(body, JsonOptions)
                ?? throw new JsonException("钉钉响应为空。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpstreamException("dingtalk_timeout", "等待钉钉响应超时，请稍后重试。", statusCode: 504);
        }
        catch (HttpRequestException)
        {
            throw new UpstreamException("dingtalk_unavailable", "暂时无法连接钉钉服务。");
        }
        catch (JsonException)
        {
            throw new UpstreamException("dingtalk_invalid_response", "钉钉响应格式不符合约定。");
        }
    }

    /// <summary>从错误 JSON 中读取不含空格或敏感文本的短错误码。</summary>
    private static string? ReadSafeCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("code", out var value)
                && !document.RootElement.TryGetProperty("errcode", out value)) return null;
            var code = value.ToString();
            return Regex.IsMatch(code, "^[A-Za-z0-9_.-]{1,80}$", RegexOptions.CultureInvariant) ? code : null;
        }
        catch (JsonException) { return null; }
    }
}
