using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OfficeMcp.Api.Features.Attendance;
using OfficeMcp.Api.Infrastructure.DingTalk;
using OfficeMcp.Api.Infrastructure.Security;
using OfficeMcp.Api.Features.DeviceCommands;

namespace OfficeMcp.Api.Tests;

/// <summary>测试宿主：所有钉钉流量都由内存处理器拦截，不使用真实应用凭据。</summary>
internal sealed class OfficeApiFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "office-test-key-12345678901234567890";
    // 合成用户标识，仅用于内存测试，不对应真实员工。
    public const string UserId = "test-user-000001";
    public FakeDingTalkHandler Handler { get; } = new();
    public TestClock Clock { get; } = new();
    public Action<AttendanceOptions>? ConfigureAttendance { get; set; }

    /// <summary>替换外部 HTTP 和时间，并覆盖本机配置，保证测试互不影响。</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(Handler));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.PostConfigure<ApiKeyOptions>(x => x.ApiKey = ApiKey);
            services.PostConfigure<DingTalkOptions>(x =>
            {
                x.ClientId = "test-client-id";
                x.ClientSecret = "test-client-secret";
            });
            services.PostConfigure<AttendanceOptions>(x =>
            {
                x.UserId = UserId;
                ConfigureAttendance?.Invoke(x);
            });
            // 既有查询测试不启动真实的远程设备工作流。
            services.PostConfigure<DeviceCommandOptions>(x => x.Enabled = false);
        });
    }

    /// <summary>创建携带测试 API Key 的调用客户端。</summary>
    public HttpClient AuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    private sealed class FakeHttpClientFactory(FakeDingTalkHandler handler) : IHttpClientFactory
    {
        /// <summary>复用可观测的内存处理器，不打开任何外部连接。</summary>
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

/// <summary>可推进时钟，用于无需等待的令牌过期测试。</summary>
internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    /// <summary>模拟时间流逝，触发刷新窗口。</summary>
    public void Advance(TimeSpan elapsed) => _now += elapsed;
}

/// <summary>记录请求次数与请求体，并返回各测试设定的钉钉响应。</summary>
internal sealed class FakeDingTalkHandler : HttpMessageHandler
{
    private int _tokenCalls;
    private int _attendanceCalls;
    public int TokenCalls => _tokenCalls;
    public int AttendanceCalls => _attendanceCalls;
    public ConcurrentQueue<string> Bodies { get; } = new();
    public ConcurrentQueue<string> Queries { get; } = new();
    public Func<int, string> AttendanceResponse { get; set; } = _ => """
        {"errcode":0,"recordresult":[]}
        """;
    public ConcurrentQueue<(string Path, string Body)> DirectoryRequests { get; } = new();
    public Func<string, string, string> DirectoryResponse { get; set; } = (path, _) =>
        path.EndsWith("/listsub", StringComparison.Ordinal)
            ? """{"errcode":0,"result":[]}"""
            : """{"errcode":0,"result":{"has_more":false,"list":[]}}""";
    public string? TokenResponseOverride { get; set; }
    public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;
    public bool SimulateTimeout { get; set; }
    public Func<string, CancellationToken, Task<string>>? AttendanceResponseAsync { get; set; }

    /// <summary>按路径区分令牌和考勤请求；意外地址立即失败。</summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath == "/v1.0/oauth2/accessToken")
        {
            Assert.Equal("api.dingtalk.com", request.RequestUri.Host);
            Assert.Equal(HttpMethod.Post, request.Method);
            var number = Interlocked.Increment(ref _tokenCalls);
            await Task.Delay(15, cancellationToken);
            return Json(TokenResponseOverride ?? $$"""{"accessToken":"test-token-{{number}}","expireIn":7200}""");
        }
        Assert.Equal("oapi.dingtalk.com", request.RequestUri.Host);
        if (request.RequestUri.AbsolutePath.StartsWith("/topapi/v2/", StringComparison.Ordinal))
        {
            var directoryBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            DirectoryRequests.Enqueue((request.RequestUri.AbsolutePath, directoryBody));
            return Json(DirectoryResponse(request.RequestUri.AbsolutePath, directoryBody));
        }
        if (request.RequestUri.AbsolutePath == "/topapi/attendance/getupdatedata")
            return Json("""{"errcode":0,"result":{"userid":"test-user-000001","attendance_result_list":[]}}""");
        Assert.Equal("/attendance/listRecord", request.RequestUri.AbsolutePath);
        Assert.Equal(HttpMethod.Post, request.Method);
        var count = Interlocked.Increment(ref _attendanceCalls);
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        Bodies.Enqueue(body);
        Queries.Enqueue(request.RequestUri.Query);
        if (SimulateTimeout) throw new TaskCanceledException("模拟上游超时");
        if (AttendanceResponseAsync is not null)
            return Json(await AttendanceResponseAsync(body, cancellationToken), ResponseStatus);
        return Json(AttendanceResponse(count), ResponseStatus);
    }

    /// <summary>生成 JSON HTTP 响应。</summary>
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
