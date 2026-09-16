using System.Net;
using System.Text.Json;

namespace OfficeMcp.Api.Tests;

/// <summary>覆盖真实 HTTP 管线、鉴权、参数契约、上游错误及数据整理。</summary>
public sealed class AttendanceApiTests
{
    /// <summary>只有存活检查可匿名访问，业务和文档均要求密钥。</summary>
    [Fact]
    public async Task OnlyHealthIsAnonymous()
    {
        await using var factory = new OfficeApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        foreach (var path in new[] { "/api/attendance?start_date=2026-09-10&end_date=2026-09-10", "/openapi/v1.json" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/attendance?start_date=2026-09-10&end_date=2026-09-10")).StatusCode);
        Assert.Equal(0, factory.Handler.TokenCalls);
    }

    /// <summary>无效日期在访问钉钉之前失败，避免产生无意义外部请求。</summary>
    [Theory]
    [InlineData("", "start_date")]
    [InlineData("?work_date=2026-09-10", "start_date")]
    [InlineData("?start_date=&end_date=2026-09-10", "start_date")]
    [InlineData("?start_date=2026-09-10", "end_date")]
    [InlineData("?start_date=2026-02-30&end_date=2026-03-01", "start_date")]
    [InlineData("?start_date=2026-09-10&end_date=2026-9-11", "end_date")]
    [InlineData("?start_date=2026-09-10T00:00:00&end_date=2026-09-10", "start_date")]
    [InlineData("?start_date=2026-09-10&start_date=2026-09-11&end_date=2026-09-11", "start_date")]
    [InlineData("?start_date=2026-09-10&end_date=2026-09-11&end_date=2026-09-12", "end_date")]
    [InlineData("?start_date=2026-09-11&end_date=2026-09-10", "end_date")]
    [InlineData("?start_date=2026-08-01&end_date=2026-09-01", "end_date")]
    public async Task InvalidDateDoesNotCallDingTalk(string query, string parameter)
    {
        await using var factory = new OfficeApiFactory();
        using var client = factory.AuthenticatedClient();
        var response = await client.GetAsync("/api/attendance" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(parameter, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.Handler.TokenCalls);
    }

    /// <summary>跨天、多班次、毫秒时间戳、缺卡和未知状态都得到保留，个人地址被过滤。</summary>
    [Fact]
    public async Task NormalizesAllRecordsWithoutChangingAttendanceDecisions()
    {
        await using var factory = new OfficeApiFactory();
        var epoch = new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
        factory.Handler.AttendanceResponse = _ => $$$"""
        {"errcode":0,"result":{"userid":"test-user-000001","attendance_result_list":[
          {"check_type":"OffDuty","plan_check_time":"2026-09-11 01:00:00","user_check_time":"2026-09-11 01:01:00","time_result":"Normal","user_address":"PRIVATE-ADDRESS"},
          {"check_type":"OnDuty","plan_check_time":"{{{epoch}}}","user_check_time":{{{epoch}}},"time_result":"Late"},
          {"check_type":"OffDuty","plan_check_time":"2026-09-10 12:00:00","user_check_time":0,"time_result":"NotSigned"},
          {"check_type":"OnDuty","plan_check_time":"2026-09-10 13:00:00","user_check_time":null,"time_result":"NewProviderState"}
        ],"approve_list":[{"tag_name":"PRIVATE-APPROVAL"}]}}
        """;
        using var client = factory.AuthenticatedClient();
        var response = await client.GetAsync("/api/attendance?start_date=2026-09-10&end_date=2026-09-10&userid=someone-else");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = Assert.Single(document.RootElement.EnumerateArray());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("tes**********001", root.GetProperty("user_id").GetString());
        Assert.Equal("2026-09-10", root.GetProperty("work_date").GetString());
        Assert.Equal("Asia/Shanghai", root.GetProperty("time_zone").GetString());
        var records = root.GetProperty("records");
        Assert.Equal(4, records.GetArrayLength());
        Assert.Equal("迟到", records[0].GetProperty("status").GetString());
        Assert.Equal("2026-09-10T09:00:00+08:00", records[0].GetProperty("actual_check_time").GetString());
        Assert.Equal(JsonValueKind.Null, records[1].GetProperty("actual_check_time").ValueKind);
        Assert.Equal("未打卡", records[1].GetProperty("status").GetString());
        Assert.Equal("NewProviderState", records[2].GetProperty("status_code").GetString());
        Assert.Equal("未知状态", records[2].GetProperty("status").GetString());
        Assert.Equal("2026-09-11T01:01:00+08:00", records[3].GetProperty("actual_check_time").GetString());
        Assert.DoesNotContain("PRIVATE", body);
        using var sent = JsonDocument.Parse(Assert.Single(factory.Handler.Bodies));
        Assert.Equal(OfficeApiFactory.UserId, sent.RootElement.GetProperty("userid").GetString());
        Assert.Equal("2026-09-10 00:00:00", sent.RootElement.GetProperty("work_date").GetString());
    }

    /// <summary>上游无数据时返回空列表，不推断休息或旷工。</summary>
    [Fact]
    public async Task EmptyResultsStayEmpty()
    {
        await using var factory = new OfficeApiFactory();
        using var client = factory.AuthenticatedClient();
        var response = await client.GetAsync("/api/attendance?start_date=2026-09-10&end_date=2026-09-10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var day = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(0, day.GetProperty("records").GetArrayLength());
        Assert.True(day.GetProperty("success").GetBoolean());
        Assert.False(day.TryGetProperty("error", out _));
    }

    /// <summary>业务拒绝、损坏响应和用户不匹配返回当日失败，不能伪装成成功的空考勤。</summary>
    [Theory]
    [InlineData("{\"errcode\":60011,\"errmsg\":\"PRIVATE-ERROR\"}", "dingtalk_api_error")]
    [InlineData("{\"result\":{}}", "dingtalk_invalid_response")]
    [InlineData("{\"errcode\":0}", "dingtalk_invalid_response")]
    [InlineData("not-json", "dingtalk_invalid_response")]
    [InlineData("{\"errcode\":0,\"result\":{\"userid\":\"another-user\"}}", "dingtalk_user_mismatch")]
    [InlineData("{\"errcode\":0,\"result\":{\"attendance_result_list\":[{\"plan_check_time\":\"invalid-time\"}]}}", "dingtalk_invalid_response")]
    public async Task UpstreamFailuresAreSafeErrors(string payload, string expectedCode)
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponse = _ => payload;
        using var client = factory.AuthenticatedClient();
        var response = await client.GetAsync("/api/attendance?start_date=2026-09-10&end_date=2026-09-10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var day = Assert.Single(document.RootElement.EnumerateArray());
        Assert.False(day.GetProperty("success").GetBoolean());
        Assert.Equal(expectedCode, day.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("tes**********001", day.GetProperty("user_id").GetString());
        Assert.DoesNotContain("PRIVATE-ERROR", body);
        Assert.DoesNotContain("test-token", body);
        Assert.Equal(1, factory.Handler.AttendanceCalls);
    }

    /// <summary>超时与普通 HTTP 失败在每日错误对象中使用不同的稳定错误码。</summary>
    [Theory]
    [InlineData(true, "dingtalk_timeout")]
    [InlineData(false, "dingtalk_http_error")]
    public async Task TransportFailuresHaveStableStatus(bool timeout, string code)
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.SimulateTimeout = timeout;
        factory.Handler.ResponseStatus = HttpStatusCode.ServiceUnavailable;
        using var client = factory.AuthenticatedClient();
        var response = await client.GetAsync("/api/attendance?start_date=2026-09-10&end_date=2026-09-10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(code, await response.Content.ReadAsStringAsync());
    }

    /// <summary>网关文档必须和实际参数、返回命名、认证方式保持一致。</summary>
    [Fact]
    public async Task OpenApiMatchesTheGatewayContract()
    {
        await using var factory = new OfficeApiFactory();
        using var client = factory.AuthenticatedClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var root = document.RootElement;
        var operation = root.GetProperty("paths").GetProperty("/api/attendance").GetProperty("get");
        Assert.Equal("attendance_query", operation.GetProperty("operationId").GetString());
        var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(new[] { "start_date", "end_date" }, parameters.Select(x => x.GetProperty("name").GetString()));
        Assert.All(parameters, parameter =>
        {
            Assert.True(parameter.GetProperty("required").GetBoolean());
            Assert.Equal("date", parameter.GetProperty("schema").GetProperty("format").GetString());
        });
        var responseSchema = operation.GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        Assert.Equal("array", responseSchema.GetProperty("type").GetString());
        Assert.EndsWith("/AttendanceResponse", responseSchema.GetProperty("items").GetProperty("$ref").GetString());
        var schemas = root.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.GetProperty("AttendanceResponse").GetProperty("properties").TryGetProperty("work_date", out _));
        Assert.True(schemas.GetProperty("AttendanceRecord").GetProperty("properties").TryGetProperty("actual_check_time", out _));
        Assert.Equal("X-Api-Key", root.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey").GetProperty("name").GetString());
        Assert.False(root.GetProperty("paths").TryGetProperty("/healthz", out _));
    }
}
