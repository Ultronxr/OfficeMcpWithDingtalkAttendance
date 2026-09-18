using System.Globalization;
using System.Text.Json;
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
    public Task<T> PostAsync<T>(string path, object body, CancellationToken cancellationToken) where T : class
    {
        if (!path.StartsWith("topapi/", StringComparison.Ordinal)
            || path.Contains("..", StringComparison.Ordinal) || path.IndexOfAny(['?', '#', '\\', ':']) >= 0)
            throw new ArgumentException("需要固定的 topapi 相对路径。", nameof(path));
        return PostCoreAsync<T>(path, body, useRecordResult: false, cancellationToken);
    }

    /// <summary>调用固定的打卡明细接口，读取顶层 recordresult，保留未知明细字段。</summary>
    /// <param name="body">包含员工 ID 和不超过七天的日期范围的请求体。</param>
    /// <param name="cancellationToken">调用方取消标记。</param>
    public Task<JsonElement[]> PostAttendanceRecordsAsync(object body, CancellationToken cancellationToken) =>
        PostCoreAsync<JsonElement[]>("attendance/listRecord", body, useRecordResult: true, cancellationToken);

    /// <summary>共享令牌刷新与业务错误处理；结果字段仅由固定接口方法选择。</summary>
    /// <typeparam name="T">对应接口的业务结果模型。</typeparam>
    /// <param name="path">已校验或硬编码的固定相对路径。</param>
    /// <param name="body">接口请求体。</param>
    /// <param name="useRecordResult">仅打卡明细接口读取 recordresult，其余接口读取 result。</param>
    /// <param name="cancellationToken">调用方取消标记。</param>
    private async Task<T> PostCoreAsync<T>(string path, object body, bool useRecordResult,
        CancellationToken cancellationToken) where T : class
    {
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
                return (useRecordResult ? response.RecordResult : response.Result)
                    ?? throw new UpstreamException("dingtalk_invalid_response", "钉钉响应缺少业务结果。");

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
        [JsonPropertyName("recordresult")] public T? RecordResult { get; init; }
    }
}
