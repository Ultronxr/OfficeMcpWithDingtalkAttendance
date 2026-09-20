using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OfficeMcp.Api.Features.Attendance;
using OfficeMcp.Api.Features.DeviceCommands;

namespace OfficeMcp.Api.Tests;

/// <summary>加班工具通过真实 HTTP 管线复用合成考勤数据，验证配置口径、兼容性与失败隔离。</summary>
public sealed class OvertimeQueryTests
{
    private const string Endpoint = "/api/attendance/overtime";
    private const string SingleDay = Endpoint + "?start_date=2026-09-10&end_date=2026-09-10";

    /// <summary>生成一条合成打卡明细；计划时间刻意与配置不同，避免意外使用钉钉计划计算。</summary>
    private static object Record(string date, string? actual, string type = "OffDuty", string userId = OfficeApiFactory.UserId,
        long id = 1, string status = "Normal") => new
    {
        id, userId, workDate = date + " 00:00:00", checkType = type,
        planCheckTime = date + " 23:00:00", userCheckTime = actual, timeResult = status,
        userAddress = "合成测试地址", futureField = new { preserved = true }
    };

    /// <summary>使用同一 listRecord 响应格式，不提供第二套打卡接口。</summary>
    private static string Records(params object[] records) => JsonSerializer.Serialize(new { errcode = 0, recordresult = records });

    /// <summary>解析 snake_case 的公共响应，所有请求均由测试 HTTP 宿主处理。</summary>
    private static async Task<OvertimeResponse> Query(HttpClient client, string path = SingleDay) =>
        (await client.GetFromJsonAsync<OvertimeResponse>(path, DeviceCommandStore.JsonOptions))!;

    /// <summary>包含 21:00 边界；满足后从 18:00 算时长，跨午夜仍归属原工作日。</summary>
    [Theory]
    [InlineData("2026-09-10 20:59:59.999", 0, "0", "0")]
    [InlineData("2026-09-10 21:00:00", 1, "10800", "3")]
    [InlineData("2026-09-10 21:00:00.123", 1, "10800.123", "3")]
    [InlineData("2026-09-10 21:30:00", 1, "12600", "3.5")]
    [InlineData("2026-09-11 00:30:00", 1, "23400", "6.5")]
    public async Task UsesInclusiveConfiguredThresholdAndCountsFromWorkEnd(string actual, int days, string seconds, string hours)
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponse = _ => Records(Record("2026-09-10", actual));
        using var client = factory.AuthenticatedClient();
        var result = await Query(client);
        Assert.Equal(days, result.Days.Count);
        Assert.Equal(days, result.Summary.OvertimeDays);
        Assert.Equal(decimal.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture), result.Summary.TotalOvertimeSeconds);
        Assert.Equal(decimal.Parse(hours, System.Globalization.CultureInfo.InvariantCulture), result.Summary.TotalOvertimeHours);
        Assert.True(result.Complete);
        Assert.Empty(result.Errors);
        Assert.True(result.Rule.ThresholdInclusive);
        Assert.Equal("work_end", result.Rule.DurationBasis);
        if (days != 0)
        {
            var day = Assert.Single(result.Days);
            Assert.Equal(new DateOnly(2026, 9, 10), day.WorkDate);
            Assert.Equal("2026-09-10T18:00:00+08:00", day.Overtime.NormalWorkEnd.ToString("yyyy-MM-ddTHH:mm:sszzz"));
            Assert.Equal(result.Summary.TotalOvertimeSeconds, day.Overtime.OvertimeSeconds);
            Assert.Equal(TimeSpan.FromHours(8), day.Overtime.LastOffDutyAt.Offset);
        }
        Assert.Equal(1, factory.Handler.AttendanceCalls);
        Assert.Equal(0, factory.Handler.VerificationCalls);
    }

    /// <summary>缺下班卡直接排除；晚上的上班卡、空时间和零时间戳不参与加班判断。</summary>
    [Fact]
    public async Task ExcludesDaysWithoutActualOffDutyTime()
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponse = _ => Records(
            Record("2026-09-10", "2026-09-10 23:00:00", "OnDuty"),
            Record("2026-09-11", null, id: 2, status: "NotSigned"),
            Record("2026-09-12", "0", id: 3),
            Record("2026-09-13", "2026-09-13 20:59:59", id: 4));
        using var client = factory.AuthenticatedClient();
        var result = await Query(client, Endpoint + "?start_date=2026-09-10&end_date=2026-09-14");
        Assert.Empty(result.Days);
        Assert.Equal(0, result.Summary.TotalOvertimeSeconds);
        Assert.Equal(0, result.Summary.OvertimeDays);
        Assert.True(result.Complete);
    }

    /// <summary>同一天只计一次，用最晚下班卡；不要求上班卡，也不以未知考勤状态覆盖用户的时间口径。</summary>
    [Fact]
    public async Task ChoosesLatestOffDutyAndPreservesEveryAttendanceRecord()
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponse = _ => Records(
            Record("2026-09-10", "2026-09-10 22:00:00", id: 3, status: "FutureStatus"),
            Record("2026-09-10", "2026-09-10 08:58:00", "OnDuty", id: 1),
            Record("2026-09-10", "2026-09-10 18:10:00", id: 2),
            Record("2026-09-10", "2026-09-10 21:00:00", id: 4));
        using var client = factory.AuthenticatedClient();
        var result = await Query(client);
        var day = Assert.Single(result.Days);
        Assert.Equal(4, day.Records.Count);
        Assert.Equal(4m, day.Overtime.OvertimeHours);
        Assert.Equal(1, result.Summary.OvertimeDays);
        Assert.Contains(day.Records, record => record.CheckType == "OnDuty");
        Assert.Contains(day.Records, record => record.StatusCode == "FutureStatus");
        // 旧端点仍返回所有日期，且原计划时间不被固定作息改写。
        var original = (await client.GetFromJsonAsync<AttendanceResponse[]>(
            "/api/attendance?start_date=2026-09-10&end_date=2026-09-11", DeviceCommandStore.JsonOptions))!;
        Assert.Equal(2, original.Length);
        Assert.Equal(4, original[0].Records.Count);
        Assert.Equal(23, original[0].Records[0].PlannedCheckTime!.Value.Hour);
        Assert.Empty(original[1].Records);
    }

    /// <summary>配置覆盖同时改变门槛与计时起点，上班时间只展示，不限制缺上班卡的日期。</summary>
    [Fact]
    public async Task ConfigurationOverridesControlTheRule()
    {
        await using var factory = new OfficeApiFactory();
        factory.ConfigureOvertime = options =>
        {
            options.WorkStartTime = new(8, 0);
            options.WorkEndTime = new(17, 0);
            options.ThresholdTime = new(20, 0);
        };
        factory.Handler.AttendanceResponse = _ => Records(Record("2026-09-10", "2026-09-10 20:00:00"));
        using var client = factory.AuthenticatedClient();
        var result = await Query(client);
        Assert.Equal(new TimeOnly(20, 0), result.Rule.ThresholdTime);
        var day = Assert.Single(result.Days);
        Assert.Equal(8, day.Overtime.NormalWorkStart.Hour);
        Assert.Equal(17, day.Overtime.NormalWorkEnd.Hour);
        Assert.Equal(3m, day.Overtime.OvertimeHours);
    }

    /// <summary>错误配置在启动时被拒绝，不以反向或跨日固定窗口悄悄计算。</summary>
    [Theory]
    [InlineData(18, 18, 21)]
    [InlineData(9, 18, 17)]
    public async Task RejectsInvalidConfiguredTimeOrder(int start, int end, int threshold)
    {
        await using var factory = new OfficeApiFactory();
        factory.ConfigureOvertime = options =>
        {
            options.WorkStartTime = new(start, 0);
            options.WorkEndTime = new(end, 0);
            options.ThresholdTime = new(threshold, 0);
        };
        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("Overtime", error.Message);
    }

    /// <summary>先合计原始秒数再舍入汇总，不能累加各日展示的舍入小时数。</summary>
    [Fact]
    public async Task SumsExactDurationsBeforeRoundingHours()
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponse = _ => Records(
            Record("2026-09-10", "2026-09-10 21:00:18"),
            Record("2026-09-11", "2026-09-11 21:00:18", id: 2));
        using var client = factory.AuthenticatedClient();
        var result = await Query(client, Endpoint + "?start_date=2026-09-10&end_date=2026-09-11");
        Assert.All(result.Days, day => Assert.Equal(3.01m, day.Overtime.OvertimeHours));
        Assert.Equal(21636m, result.Summary.TotalOvertimeSeconds);
        Assert.Equal(6.01m, result.Summary.TotalOvertimeHours);
        Assert.Equal(new[] { new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 11) }, result.Days.Select(day => day.WorkDate));
    }

    /// <summary>保留 simple/full 的原始明细隔离；摘要不暴露完整员工 ID。</summary>
    [Theory]
    [InlineData("simple", false)]
    [InlineData("full", true)]
    public async Task ReusesOriginalDetailModes(string detail, bool full)
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponse = _ => Records(Record("2026-09-10", "2026-09-10 21:30:00"));
        using var client = factory.AuthenticatedClient();
        var response = await client.GetAsync(SingleDay + "&detail=" + detail);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<OvertimeResponse>(body, DeviceCommandStore.JsonOptions)!;
        var record = Assert.Single(Assert.Single(result.Days).Records);
        Assert.Equal(full, record.Details.HasValue);
        if (full) Assert.True(record.Details!.Value.GetProperty("futureField").GetProperty("preserved").GetBoolean());
        else
        {
            Assert.DoesNotContain("合成测试地址", body);
            Assert.DoesNotContain(OfficeApiFactory.UserId, body);
        }
    }

    /// <summary>默认员工、指定 ID 和姓名全部走原选择流程，加班工具不另行读取员工目录。</summary>
    [Theory]
    [InlineData("", OfficeApiFactory.UserId, false)]
    [InlineData("&user_id=synthetic-other-id", "synthetic-other-id", false)]
    [InlineData("&user_name=李四", "synthetic-other-id", true)]
    public async Task ReusesEmployeeSelection(string query, string userId, bool directoryExpected)
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.DirectoryResponse = (path, _) => path.EndsWith("/listsub", StringComparison.Ordinal)
            ? """{"errcode":0,"result":[]}"""
            : """{"errcode":0,"result":{"has_more":false,"list":[{"userid":"synthetic-other-id","name":"李四"}]}}""";
        factory.Handler.AttendanceResponse = _ => Records(Record("2026-09-10", "2026-09-10 21:30:00", userId: userId));
        using var client = factory.AuthenticatedClient();
        var result = await Query(client, SingleDay + query);
        using var sent = JsonDocument.Parse(Assert.Single(factory.Handler.Bodies));
        Assert.Equal(userId, sent.RootElement.GetProperty("userIds")[0].GetString());
        Assert.Equal(directoryExpected, !factory.Handler.DirectoryRequests.IsEmpty);
        Assert.Equal(directoryExpected ? "李四" : null, Assert.Single(result.Days).UserName);
    }

    /// <summary>跨七天只调用原分段查询；失败日期单列，成功加班日和部分汇总仍可用。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DoesNotTreatFailedQueriesAsNoOvertime(bool allFailed)
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponseAsync = (body, _) =>
        {
            using var request = JsonDocument.Parse(body);
            var first = request.RootElement.GetProperty("checkDateFrom").GetString()!.StartsWith("2026-09-10", StringComparison.Ordinal);
            return Task.FromResult(!allFailed && first ? Records(Record("2026-09-10", "2026-09-10 21:30:00"))
                : """{"errcode":60011,"errmsg":"PRIVATE-UPSTREAM-ERROR"}""");
        };
        using var client = factory.AuthenticatedClient();
        var result = await Query(client, Endpoint + "?start_date=2026-09-10&end_date=2026-09-17");
        Assert.Equal(2, factory.Handler.AttendanceCalls);
        Assert.False(result.Complete);
        Assert.Equal(allFailed ? 8 : 1, result.Errors.Count);
        Assert.Equal(allFailed ? 0 : 1, result.Days.Count);
        Assert.Equal(allFailed ? 0m : 3.5m, result.Summary.TotalOvertimeHours);
        Assert.All(result.Errors, error =>
        {
            Assert.Equal("60011", error.Error.ProviderCode);
            Assert.DoesNotContain("PRIVATE", error.Error.Message);
            Assert.DoesNotContain(OfficeApiFactory.UserId, error.UserId);
        });
    }

    /// <summary>日期、员工与明细参数共用严格验证，非法调用不发送任何钉钉请求。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("?start_date=2026-09-10")]
    [InlineData("?start_date=2026-09-11&end_date=2026-09-10")]
    [InlineData("?start_date=2026-08-01&end_date=2026-09-10")]
    [InlineData("?start_date=2026-9-10&end_date=2026-09-10")]
    [InlineData("?start_date=2026-09-10&start_date=2026-09-10&end_date=2026-09-10")]
    [InlineData("?start_date=2026-09-10&end_date=2026-09-10&user_id=a&user_name=b")]
    [InlineData("?start_date=2026-09-10&end_date=2026-09-10&detail=unknown")]
    [InlineData("?start_date=2026-09-10&end_date=2026-09-10&detail=full&detail=simple")]
    public async Task RejectsInvalidQueryWithoutCallingDingTalk(string query)
    {
        await using var factory = new OfficeApiFactory();
        using var client = factory.AuthenticatedClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Endpoint + query)).StatusCode);
        Assert.Equal(0, factory.Handler.TokenCalls);
    }

    /// <summary>新工具受原办公认证保护，OpenAPI 保留同样五个参数并声明加班结果。</summary>
    [Fact]
    public async Task AuthenticationAndOpenApiDescribeTheNewTool()
    {
        await using var factory = new OfficeApiFactory();
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(SingleDay)).StatusCode);
        using var client = factory.AuthenticatedClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var operation = document.RootElement.GetProperty("paths").GetProperty(Endpoint).GetProperty("get");
        Assert.Equal("attendance_overtime", operation.GetProperty("operationId").GetString());
        var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(new[] { "start_date", "end_date", "user_id", "user_name", "detail" }, parameters.Select(x => x.GetProperty("name").GetString()));
        Assert.All(parameters.Take(2), parameter => Assert.True(parameter.GetProperty("required").GetBoolean()));
        Assert.Contains("21:00:00", operation.GetProperty("description").GetString());
        var response = operation.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.EndsWith("/OvertimeResponse", response.GetProperty("$ref").GetString());
        var properties = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("OvertimeResponse").GetProperty("properties");
        Assert.True(properties.TryGetProperty("days", out _));
        Assert.True(properties.TryGetProperty("complete", out _));
        Assert.True(properties.TryGetProperty("errors", out _));
    }
}
