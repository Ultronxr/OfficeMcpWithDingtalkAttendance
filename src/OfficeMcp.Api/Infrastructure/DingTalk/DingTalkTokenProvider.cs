using Microsoft.Extensions.Options;
using OfficeMcp.Api.Infrastructure.Errors;

namespace OfficeMcp.Api.Infrastructure.DingTalk;

/// <summary>缓存企业内部应用令牌，并用进程内锁合并并发刷新。</summary>
public sealed class DingTalkTokenProvider(DingTalkTransport transport, IOptions<DingTalkOptions> options,
    TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private TokenLease? _cached;

    /// <summary>获取可用令牌；提供被拒令牌时只刷新仍在使用的旧版本。</summary>
    /// <param name="cancellationToken">取消等待或外部请求。</param>
    /// <param name="rejectedToken">钉钉已明确拒绝的令牌，用于一次受控刷新。</param>
    public async Task<string> GetAsync(CancellationToken cancellationToken, string? rejectedToken = null)
    {
        // 双重检查保证多个业务模块同时请求时只向钉钉获取一次令牌。
        var cached = Volatile.Read(ref _cached);
        if (IsUsable(cached, rejectedToken)) return cached!.Value;
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            cached = Volatile.Read(ref _cached);
            if (IsUsable(cached, rejectedToken)) return cached!.Value;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dingtalk.com/v1.0/oauth2/accessToken")
            {
                Content = JsonContent.Create(new { appKey = options.Value.ClientId, appSecret = options.Value.ClientSecret })
            };
            var result = await transport.SendAsync<TokenResponse>(request, cancellationToken);
            if (string.IsNullOrWhiteSpace(result.AccessToken) || result.ExpireIn is < 1 or > 86400)
                throw new UpstreamException("dingtalk_invalid_token_response", "钉钉未返回有效的应用令牌。");

            // 提前最多 5 分钟刷新；短有效期令牌也保留 90% 的可用时间。
            var refreshAfter = result.ExpireIn - Math.Min(300, result.ExpireIn / 10.0);
            Volatile.Write(ref _cached, new TokenLease(result.AccessToken, clock.GetUtcNow().AddSeconds(refreshAfter)));
            return result.AccessToken;
        }
        finally { _refreshLock.Release(); }
    }

    /// <summary>判断缓存是否尚未进入刷新窗口且未被当前请求拒绝。</summary>
    private bool IsUsable(TokenLease? token, string? rejectedToken) =>
        token is not null && clock.GetUtcNow() < token.RefreshAt && token.Value != rejectedToken;

    /// <summary>宿主关闭时释放令牌刷新锁。</summary>
    public void Dispose() => _refreshLock.Dispose();

    private sealed record TokenLease(string Value, DateTimeOffset RefreshAt);
    private sealed class TokenResponse
    {
        public string? AccessToken { get; init; }
        public int ExpireIn { get; init; }
    }
}
