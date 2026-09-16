using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OfficeMcp.Api.Infrastructure.DingTalk;

namespace OfficeMcp.Api.Tests;

/// <summary>验证并发缓存、提前过期和有界重试。</summary>
public sealed class TokenCacheTests
{
    /// <summary>并发冷启动只获取一个令牌，进入刷新窗口后再获取一个。</summary>
    [Fact]
    public async Task ConcurrentQueriesShareOneTokenAndRefreshBeforeExpiry()
    {
        await using var factory = new OfficeApiFactory();
        using var client = factory.AuthenticatedClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => client.GetAsync("/api/attendance?start_date=2026-09-10&end_date=2026-09-10")));
        Assert.All(responses, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
        Assert.Equal(1, factory.Handler.TokenCalls);
        factory.Clock.Advance(TimeSpan.FromSeconds(6901));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/attendance?start_date=2026-09-10&end_date=2026-09-10")).StatusCode);
        Assert.Equal(2, factory.Handler.TokenCalls);
    }

    /// <summary>失效令牌只刷新一次，第二次仍失败时向调用者返回错误。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedTokenRetriesAtMostOnce(bool alwaysReject)
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponse = count => alwaysReject || count == 1
            ? "{\"errcode\":42001}"
            : "{\"errcode\":0,\"result\":{\"attendance_result_list\":[]}}";
        using var client = factory.AuthenticatedClient();
        var response = await client.GetAsync("/api/attendance?start_date=2026-09-10&end_date=2026-09-10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(!alwaysReject, document.RootElement[0].GetProperty("success").GetBoolean());
        Assert.Equal(2, factory.Handler.TokenCalls);
        Assert.Equal(2, factory.Handler.AttendanceCalls);
        Assert.Contains("test-token-2", factory.Handler.Queries.Last());
    }

    /// <summary>迟到的旧令牌失败响应不能使另一个请求刚刷新的令牌失效。</summary>
    [Fact]
    public async Task StaleRejectionDoesNotInvalidateNewToken()
    {
        await using var factory = new OfficeApiFactory();
        _ = factory.CreateClient();
        var tokens = factory.Services.GetRequiredService<DingTalkTokenProvider>();
        var first = await tokens.GetAsync(CancellationToken.None);
        var second = await tokens.GetAsync(CancellationToken.None, first);
        var third = await tokens.GetAsync(CancellationToken.None, first);
        Assert.NotEqual(first, second);
        Assert.Equal(second, third);
        Assert.Equal(2, factory.Handler.TokenCalls);
    }

    /// <summary>损坏令牌响应不能被缓存，也不能继续查询业务接口。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"accessToken\":\"test-token\",\"expireIn\":0}")]
    public async Task InvalidTokenNeverReachesBusinessApi(string tokenResponse)
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.TokenResponseOverride = tokenResponse;
        using var client = factory.AuthenticatedClient();
        var response = await client.GetAsync("/api/attendance?start_date=2026-09-10&end_date=2026-09-10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(document.RootElement[0].GetProperty("success").GetBoolean());
        Assert.Equal(0, factory.Handler.AttendanceCalls);
    }
}
