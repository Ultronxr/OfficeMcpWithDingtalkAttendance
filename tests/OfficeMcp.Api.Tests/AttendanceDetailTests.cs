using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OfficeMcp.Api.Infrastructure.DingTalk;

namespace OfficeMcp.Api.Tests;

/// <summary>验证新查询入参、原始明细保留、工作日归属以及旧核验接口兼容。</summary>
public sealed class AttendanceDetailTests
{
    private const string Path = "/api/attendance?start_date=2026-09-10&end_date=2026-09-10";

    /// <summary>非法选择和明细模式在调用钉钉前拒绝。</summary>
    [Theory]
    [InlineData("&user_id=one&user_name=张三")]
    [InlineData("&user_id=one&user_id=two")]
    [InlineData("&user_name=张三&user_name=李四")]
    [InlineData("&detail=full&detail=simple")]
    [InlineData("&detail=unknown")]
    public async Task RejectsInvalidSelections(string query)
    {
        await using var factory = new OfficeApiFactory();
        using var client = factory.AuthenticatedClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Path + query)).StatusCode);
        Assert.Equal(0, factory.Handler.TokenCalls);
    }

    /// <summary>完整明细保留地址、Wi-Fi 及未知字段，摘要省略明细，并保留不同流水。</summary>
    [Theory]
    [InlineData("", false)]
    [InlineData("&detail=simple", false)]
    [InlineData("&detail=full", true)]
    public async Task FullModePreservesRawFieldsAndDeduplicatesOnlySameRecordId(string query, bool full)
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponse = _ => """
            {"errcode":0,"recordresult":[
              {"id":1,"userId":"test-user-000001","workDate":"2026-09-10 00:00:00","checkType":"OffDuty","baseCheckTime":"2026-09-11 01:00:00","userCheckTime":"2026-09-11 01:01:00","gmtModified":"2026-09-11 01:01:00","userAddress":"旧地址"},
              {"id":1,"userId":"test-user-000001","workDate":"2026-09-10 00:00:00","checkType":"OffDuty","baseCheckTime":"2026-09-11 01:00:00","userCheckTime":"2026-09-11 01:02:00","gmtModified":"2026-09-11 01:02:00","userAddress":"合成地址","userLongitude":120.1,"deviceId":"test-device","wifiMac":"test-wifi","futureField":{"keepCase":true}},
              {"id":2,"userId":"test-user-000001","workDate":"2026-09-10 00:00:00","checkType":"OffDuty","userCheckTime":"2026-09-11 01:03:00"}
            ]}
            """;
        using var client = factory.AuthenticatedClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync(Path + query));
        var day = document.RootElement[0];
        Assert.True(day.GetProperty("success").GetBoolean());
        Assert.Equal("2026-09-10", day.GetProperty("work_date").GetString());
        var records = day.GetProperty("records");
        Assert.Equal(2, records.GetArrayLength());
        Assert.Equal("2026-09-11T01:00:00+08:00", records[0].GetProperty("planned_check_time").GetString());
        Assert.Equal("2026-09-11T01:02:00+08:00", records[0].GetProperty("actual_check_time").GetString());
        Assert.Equal(full, records[0].TryGetProperty("details", out var details));
        if (full)
        {
            Assert.Equal("合成地址", details.GetProperty("userAddress").GetString());
            Assert.Equal(120.1, details.GetProperty("userLongitude").GetDouble());
            Assert.Equal("test-device", details.GetProperty("deviceId").GetString());
            Assert.Equal("test-wifi", details.GetProperty("wifiMac").GetString());
            Assert.True(details.GetProperty("futureField").GetProperty("keepCase").GetBoolean());
        }
        else Assert.DoesNotContain("userAddress", document.RootElement.GetRawText());
    }

    /// <summary>缺失工作日不能猜归属，错误工作日和员工也不能混入成功数据。</summary>
    [Theory]
    [InlineData("null", "dingtalk_invalid_response")]
    [InlineData("\"2026-09-09 00:00:00\"", "dingtalk_date_mismatch")]
    public async Task InvalidWorkDateFailsWholeSegment(string workDate, string code)
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponse = _ => $$"""{"errcode":0,"recordresult":[{"userId":"test-user-000001","workDate":{{workDate}}}]}""";
        using var client = factory.AuthenticatedClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync(Path));
        Assert.False(document.RootElement[0].GetProperty("success").GetBoolean());
        Assert.Equal(code, document.RootElement[0].GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>主动打卡的旧接口仍读取 result，不被新顶层 recordresult 解析逻辑影响。</summary>
    [Fact]
    public async Task LegacyResultEnvelopeStillWorks()
    {
        await using var factory = new OfficeApiFactory();
        _ = factory.CreateClient();
        var dingTalk = factory.Services.GetRequiredService<DingTalkClient>();
        var result = await dingTalk.PostAsync<Dictionary<string, JsonElement>>("topapi/attendance/getupdatedata",
            new { userid = OfficeApiFactory.UserId, work_date = "2026-09-10 00:00:00" }, CancellationToken.None);
        Assert.Equal(OfficeApiFactory.UserId, result["userid"].GetString());
        Assert.Equal(0, result["attendance_result_list"].GetArrayLength());
    }
}
