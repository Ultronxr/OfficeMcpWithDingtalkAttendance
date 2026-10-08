using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OfficeMcp.Api.Features.Attendance;
using OfficeMcp.Api.Features.DeviceCommands;

namespace OfficeMcp.Api.Tests;

/// <summary>使用原生报表形状和合成记录验证非工作日、缺卡与分类失败的数据流出。</summary>
public sealed class OvertimeNonWorkdayTests
{
    private const string Endpoint = "/api/attendance/overtime";
    private const string Day = "2026-09-12";
    private const long ColumnId = 91001;

    /// <summary>构造非真实员工的打卡，不使用原生加班值或时间计划代替实际记录。</summary>
    private static object Record(string date, string? time, string? type = "OnDuty", long id = 1) => new
    {
        id, userId = OfficeApiFactory.UserId, workDate = date + " 00:00:00", checkType = type,
        userCheckTime = time is null ? null : date + " " + time, planCheckTime = date + " 09:00:00",
        timeResult = "Normal", futureField = new { preserved = true }
    };

    /// <summary>合成 listRecord 原始明细包。</summary>
    private static string Records(params object[] values) => JsonSerializer.Serialize(new { errcode = 0, recordresult = values });

    /// <summary>构造原生 getcolumnval 形状；数据按动态列 ID 归属。</summary>
    private static string Values(params (string Date, object? Value)[] values) => JsonSerializer.Serialize(new
    {
        errcode = 0, result = new { column_vals = new[] { new
        {
            column_vo = new { id = ColumnId }, column_vals = values.Select(value => new { date = value.Date, value = value.Value })
        } } }
    });

    /// <summary>元数据走默认原生结构，只覆盖当前测试关注的逐日值。</summary>
    private static void Report(OfficeApiFactory factory, string values) => factory.Handler.ReportResponse = (path, body) =>
        path.EndsWith("getattcolumns", StringComparison.Ordinal) ? FakeDingTalkHandler.DefaultReport(path, body) : values;

    /// <summary>经完整 HTTP 和全局时间序列化调用原加班端点。</summary>
    private static async Task<OvertimeResponse> Query(HttpClient client, string start = Day, string? end = null, bool full = false) =>
        (await client.GetFromJsonAsync<OvertimeResponse>($"{Endpoint}?start_date={start}&end_date={end ?? start}&detail={(full ? "full" : "simple")}", DeviceCommandStore.JsonOptions))!;

    /// <summary>休息日及节假日安排为休息时，白天上下班已足够纳入；不受星期或 21 点限制。</summary>
    [Theory]
    [InlineData(Day)]
    [InlineData("2026-10-01")]
    public async Task IncludesRestAndHolidayAttendanceWithoutEveningThreshold(string date)
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponse = _ => Records(
            Record(date, "09:00:00.123", id: 1), Record(date, "18:00:00.456", "OffDuty", 2),
            Record(date, "10:00:00", id: 3), Record(date, "17:00:00", "OffDuty", 4));
        Report(factory, Values((date + " 00:00:00", "1")));
        using var client = factory.AuthenticatedClient();
        var result = await Query(client, date, full: true);
        var day = Assert.Single(result.Days);
        Assert.Equal("non_workday", day.DayType);
        Assert.True(day.OvertimeConfirmed);
        Assert.Equal("non_workday_record", day.InclusionReason);
        Assert.True(day.Classification.IsNonWorkday);
        Assert.Equal(4, day.Records.Count);
        Assert.All(day.Records, record => Assert.True(record.Details!.Value.GetProperty("futureField").GetProperty("preserved").GetBoolean()));
        Assert.Null(day.Overtime.NormalWorkEnd);
        Assert.Null(day.Overtime.ThresholdAt);
        Assert.Equal("first_on_to_last_off", day.Overtime.DurationBasis);
        Assert.Equal(32400.333m, day.Overtime.OvertimeSeconds);
        Assert.Equal(9m, day.Overtime.OvertimeHours);
        Assert.Equal("2026-09-12T09:00:00+08:00".Replace(Day, date),
            day.Overtime.FirstOnDutyAt!.Value.ToString("yyyy-MM-ddTHH:mm:sszzz"));
        Assert.Empty(day.Anomalies);
        Assert.True(result.Complete);
        Assert.True(result.Summary.DurationComplete);
        Assert.Equal(2, factory.Handler.ReportRequests.Count);
        Assert.Equal(0, factory.Handler.VerificationCalls);
    }

    /// <summary>只有一张卡、未知类型或缺实际时间都保留日期；未知时长必须为 null。</summary>
    [Theory]
    [InlineData("OnDuty", "09:00:00", "missing_off_duty", true)]
    [InlineData("OffDuty", "17:00:00", "missing_on_duty", true)]
    [InlineData("FutureType", "12:00:00", "unknown_check_type", true)]
    [InlineData(null, "12:00:00", "unknown_check_type", true)]
    [InlineData("OnDuty", null, "actual_time_unavailable", false)]
    public async Task RetainsIncompleteOrUnknownRecords(string? type, string? time, string anomaly, bool actualKnown)
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponse = _ => Records(Record(Day, time, type));
        Report(factory, Values((Day, 1)));
        using var client = factory.AuthenticatedClient();
        var result = await Query(client);
        var day = Assert.Single(result.Days);
        Assert.Single(day.Records);
        Assert.Contains(day.Anomalies, item => item.Code == anomaly);
        Assert.Null(day.Overtime.OvertimeSeconds);
        Assert.Null(day.Overtime.OvertimeHours);
        Assert.Equal("unavailable", day.Overtime.DurationStatus);
        Assert.Equal(actualKnown ? true : (bool?)null, day.OvertimeConfirmed);
        Assert.Equal(actualKnown ? 1 : 0, result.Summary.OvertimeDays);
        Assert.Equal(actualKnown ? 0 : 1, result.Summary.UnconfirmedDays);
        Assert.Equal(actualKnown ? 1 : 0, result.Summary.DurationUnconfirmedDays);
        Assert.False(result.Summary.DurationComplete);
        Assert.True(result.Complete);
    }

    /// <summary>跨午夜使用原工作日归属；逆序时仍保留但不能输出负时长。</summary>
    [Theory]
    [InlineData("2026-09-13 00:30:00", "55800", false)]
    [InlineData("2026-09-12 08:30:00", null, true)]
    public async Task PreservesCrossMidnightAndFlagsReverseOrder(string off, string? seconds, bool invalid)
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponse = _ => Records(Record(Day, "09:00:00"), new
        {
            id = 2, userId = OfficeApiFactory.UserId, workDate = Day + " 00:00:00", checkType = "OffDuty", userCheckTime = off
        });
        Report(factory, Values((Day, "1.0")));
        using var client = factory.AuthenticatedClient();
        var result = await Query(client);
        var day = Assert.Single(result.Days);
        Assert.Equal(new DateOnly(2026, 9, 12), day.WorkDate);
        Assert.Equal(seconds is null ? (decimal?)null : decimal.Parse(seconds, CultureInfo.InvariantCulture), day.Overtime.OvertimeSeconds);
        Assert.Equal(invalid, day.Anomalies.Any(item => item.Code == "invalid_check_order"));
    }

    /// <summary>周六被上游安排为工作日时仍应用原门槛，不能以星期覆盖调休安排。</summary>
    [Fact]
    public async Task DoesNotInferRestDaysFromWeekday()
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponse = _ => Records(Record(Day, "09:00:00"), Record(Day, "18:00:00", "OffDuty", 2));
        using var client = factory.AuthenticatedClient();
        var result = await Query(client);
        Assert.Empty(result.Days);
        Assert.True(result.Complete);
    }

    /// <summary>元数据缺失、重复或业务错误都保留已有打卡，原始上游错误不泄露。</summary>
    [Theory]
    [InlineData("{\"errcode\":60011,\"errmsg\":\"PRIVATE-MESSAGE\"}")]
    [InlineData("{\"errcode\":0,\"result\":{\"columns\":[]}}")]
    [InlineData("{\"errcode\":0,\"result\":{\"columns\":[{\"id\":0,\"alias\":\"attendance_rest_days\"}]}}")]
    [InlineData("{\"errcode\":0,\"result\":{\"columns\":[{\"id\":1,\"alias\":\"attendance_rest_days\"},{\"id\":2,\"alias\":\"attendance_rest_days\"}]}}")]
    public async Task MetadataFailuresPreserveCandidates(string response)
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponse = _ => Records(Record(Day, "09:00:00"));
        factory.Handler.ReportResponse = (_, _) => response;
        using var client = factory.AuthenticatedClient();
        var result = await Query(client);
        var day = Assert.Single(result.Days);
        Assert.Equal("unknown", day.DayType);
        Assert.Null(day.OvertimeConfirmed);
        Assert.Equal("classification_unavailable", day.InclusionReason);
        Assert.Single(day.Records);
        Assert.False(result.Complete);
        Assert.Equal(1, result.Summary.ReturnedDays);
        Assert.Equal(1, result.Summary.UnconfirmedDays);
        Assert.Equal(0, result.Summary.OvertimeDays);
        Assert.DoesNotContain("PRIVATE", Assert.Single(result.Errors).Error.Message);
        Assert.Single(factory.Handler.ReportRequests);
    }

    /// <summary>空、分数、布尔和负数不冒充确定的工作日分类。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"0.5\"")]
    [InlineData("true")]
    [InlineData("-1")]
    [InlineData("\"0,1\"")]
    public async Task AmbiguousRestValuesRemainVisible(string valueJson)
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponse = _ => Records(Record(Day, "18:00:00", "OffDuty"));
        using var value = JsonDocument.Parse(valueJson);
        Report(factory, Values((Day, value.RootElement.Clone())));
        using var client = factory.AuthenticatedClient();
        var result = await Query(client);
        Assert.Equal("overtime_classification_ambiguous", Assert.Single(result.Errors).Error.Code);
        Assert.Single(result.Days);
        Assert.False(result.Complete);
    }

    /// <summary>错误列、重复日期、无效日期、跨范围和空数据都不得静默隐藏已有记录。</summary>
    [Theory]
    [InlineData("{\"column_vals\":[{\"column_vo\":{\"id\":99},\"column_vals\":[]}]}")]
    [InlineData("{\"column_vals\":[{\"column_vo\":{\"id\":91001},\"column_vals\":[null]}]}")]
    [InlineData("{\"column_vals\":[{\"column_vo\":{\"id\":91001},\"column_vals\":[{\"date\":\"2026-09-12\",\"value\":\"1\"},{\"date\":\"2026-09-12\",\"value\":\"0\"}]}]}")]
    [InlineData("{\"column_vals\":[{\"column_vo\":{\"id\":91001},\"column_vals\":[{\"date\":\"2026-02-30\",\"value\":\"1\"}]}]}")]
    [InlineData("{\"column_vals\":[{\"column_vo\":{\"id\":91001},\"column_vals\":[{\"date\":\"2026-09-13\",\"value\":\"1\"}]}]}")]
    [InlineData("{\"column_vals\":[{\"column_vo\":{\"id\":91001},\"column_vals\":[]}]}")]
    public async Task InvalidOrMissingReportsDoNotHideRecords(string resultJson)
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponse = _ => Records(Record(Day, "12:00:00", null));
        Report(factory, "{\"errcode\":0,\"result\":" + resultJson + "}");
        using var client = factory.AuthenticatedClient();
        var result = await Query(client);
        Assert.Single(result.Days);
        Assert.Single(result.Errors);
        Assert.False(result.Complete);
        Assert.Equal("unknown", result.Days[0].DayType);
    }

    /// <summary>缺卡已确认日期与分类未知候选分别汇总，完整性标记不能让下游把部分时长当总时长。</summary>
    [Fact]
    public async Task SummarySeparatesKnownMissingAndUnknownDays()
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponse = _ => Records(
            Record("2026-09-10", "21:30:00", "OffDuty", 1),
            Record("2026-09-11", "18:00:00", "OffDuty", 2),
            Record("2026-09-12", "09:00:00", id: 3),
            Record("2026-09-13", "17:00:00", "OffDuty", 4),
            Record("2026-09-14", null, id: 5));
        Report(factory, Values(("2026-09-10", "0"), ("2026-09-11", "0"),
            ("2026-09-12", "1"), ("2026-09-13", "0.5"), ("2026-09-14", "1")));
        using var client = factory.AuthenticatedClient();
        var result = await Query(client, "2026-09-10", "2026-09-14");
        Assert.Equal(4, result.Days.Count);
        Assert.Equal(2, result.Summary.OvertimeDays);
        Assert.Equal(2, result.Summary.UnconfirmedDays);
        Assert.Equal(1, result.Summary.DurationUnconfirmedDays);
        Assert.Equal(3.5m, result.Summary.TotalOvertimeHours);
        Assert.False(result.Summary.DurationComplete);
        Assert.False(result.Complete);
        Assert.Single(result.Errors);
    }

    /// <summary>企业列重命名与不同 ID 不影响 alias 识别，请求必须使用运行时发现的 ID。</summary>
    [Fact]
    public async Task FindsColumnByAliasInsteadOfNameOrHardcodedId()
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponse = _ => Records(Record(Day, "09:00:00"));
        factory.Handler.ReportResponse = (path, _) => path.EndsWith("getattcolumns", StringComparison.Ordinal)
            ? """{"errcode":0,"result":{"columns":[{"id":"92123","alias":"attendance_rest_days","name":"员工自定义列名"}]}}"""
            : """{"errcode":0,"result":{"column_vals":[{"column_vo":{"id":"92123"},"column_vals":[{"date":"2026-09-11T16:00:00Z","value":"1"}]}]}}""";
        using var client = factory.AuthenticatedClient();
        var result = await Query(client);
        Assert.Equal("non_workday", Assert.Single(result.Days).DayType);
        using var sent = JsonDocument.Parse(factory.Handler.ReportRequests.Last().Body);
        Assert.Equal("92123", sent.RootElement.GetProperty("column_id_list").GetString());
        Assert.Equal(OfficeApiFactory.UserId, sent.RootElement.GetProperty("userid").GetString());
    }

    /// <summary>宽范围复用原考勤七天分段、报表最多 31 天一段，后一段失败不丢掉前一段分类。</summary>
    [Fact]
    public async Task SegmentsReportsAndPreservesEarlierClassifications()
    {
        await using var factory = new OfficeApiFactory();
        factory.ConfigureAttendance = options => options.MaxQueryDays = 65;
        var dates = new[] { "2026-09-01", "2026-10-02", "2026-10-03" };
        factory.Handler.AttendanceResponseAsync = (body, _) =>
        {
            using var request = JsonDocument.Parse(body);
            var start = request.RootElement.GetProperty("checkDateFrom").GetString()![..10];
            var end = request.RootElement.GetProperty("checkDateTo").GetString()![..10];
            return Task.FromResult(Records(dates.Where(date => string.CompareOrdinal(date, start) >= 0 && string.CompareOrdinal(date, end) <= 0)
                .SelectMany((date, index) => new[] { Record(date, "09:00:00", id: index * 2 + 1), Record(date, "18:00:00", "OffDuty", index * 2 + 2) }).ToArray()));
        };
        factory.Handler.ReportResponse = (path, body) => path.EndsWith("getattcolumns", StringComparison.Ordinal)
            ? FakeDingTalkHandler.DefaultReport(path, body)
            : body.Contains("2026-09-01", StringComparison.Ordinal) ? Values(("2026-09-01", "1"))
                : """{"errcode":60011,"errmsg":"PRIVATE"}""";
        using var client = factory.AuthenticatedClient();
        var result = await Query(client, dates[0], dates[^1]);
        Assert.Equal(3, result.Days.Count);
        Assert.Equal("non_workday", result.Days[0].DayType);
        Assert.All(result.Days.Skip(1), day => Assert.Equal("unknown", day.DayType));
        Assert.Equal(1, result.Summary.OvertimeDays);
        Assert.Equal(9m, result.Summary.TotalOvertimeHours);
        Assert.Equal(2, result.Errors.Count);
        Assert.Equal(3, factory.Handler.ReportRequests.Count);
        foreach (var sent in factory.Handler.ReportRequests.Where(item => item.Path.EndsWith("getcolumnval", StringComparison.Ordinal)))
        {
            using var doc = JsonDocument.Parse(sent.Body);
            var start = DateOnly.Parse(doc.RootElement.GetProperty("from_date").GetString()![..10]);
            var end = DateOnly.Parse(doc.RootElement.GetProperty("to_date").GetString()![..10]);
            Assert.InRange(end.DayNumber - start.DayNumber + 1, 1, 31);
        }
    }

    /// <summary>客户端主动取消必须传播，不能冒充分类失败的正常成功响应。</summary>
    [Fact]
    public async Task ClientCancellationPropagates()
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.ReportResponseAsync = async (_, _, token) => { await Task.Delay(Timeout.Infinite, token); return ""; };
        using var scope = factory.Services.CreateScope();
        using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<OvertimeClassificationService>()
            .QueryAsync([new(2026, 9, 12)], OfficeApiFactory.UserId, canceled.Token, canceled.Token));
    }

    /// <summary>分类预算耗尽保留已取回打卡，并通过未知分类、错误和汇总完整性说明。</summary>
    [Fact]
    public async Task ClassificationTimeoutRetainsAttendance()
    {
        await using var factory = new OfficeApiFactory();
        factory.ConfigureAttendance = options => options.QueryTimeoutSeconds = 1;
        factory.Handler.AttendanceResponseAsync = async (_, token) =>
        {
            await Task.Delay(600, token);
            return Records(Record(Day, "09:00:00"));
        };
        factory.Handler.ReportResponseAsync = async (path, body, token) =>
        {
            if (path.EndsWith("getattcolumns", StringComparison.Ordinal)) return FakeDingTalkHandler.DefaultReport(path, body);
            await Task.Delay(Timeout.Infinite, token);
            return "";
        };
        using var client = factory.AuthenticatedClient();
        var result = await Query(client);
        Assert.Single(Assert.Single(result.Days).Records);
        Assert.Equal("overtime_classification_timeout", Assert.Single(result.Errors).Error.Code);
        Assert.False(result.Complete);
    }

    /// <summary>无记录不请求分类；原考勤接口也不产生额外报表流量。</summary>
    [Fact]
    public async Task EmptyAttendanceAndOriginalQueryAvoidReports()
    {
        await using var factory = new OfficeApiFactory();
        using var client = factory.AuthenticatedClient();
        Assert.Empty((await Query(client)).Days);
        factory.Handler.AttendanceResponse = _ => Records(Record(Day, "09:00:00"));
        await client.GetStringAsync($"/api/attendance?start_date={Day}&end_date={Day}");
        Assert.Empty(factory.Handler.ReportRequests);
    }

    /// <summary>原始 JSON 和 OpenAPI 都声明未知时长为 null，新增异常字段供 Agent 消费。</summary>
    [Fact]
    public async Task JsonAndOpenApiExposeNullableDurationAndAnomalies()
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponse = _ => Records(Record(Day, "09:00:00"));
        Report(factory, Values((Day, "1")));
        using var client = factory.AuthenticatedClient();
        using var json = JsonDocument.Parse(await client.GetStringAsync($"{Endpoint}?start_date={Day}&end_date={Day}"));
        var day = json.RootElement.GetProperty("days")[0];
        Assert.Equal(JsonValueKind.Null, day.GetProperty("overtime").GetProperty("overtime_hours").ValueKind);
        Assert.True(day.GetProperty("overtime_confirmed").GetBoolean());
        Assert.True(day.GetProperty("anomalies").GetArrayLength() > 0);
        using var api = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var schemas = api.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.GetProperty("OvertimeInfo").GetProperty("properties").GetProperty("overtime_hours").GetProperty("nullable").GetBoolean());
        var description = api.RootElement.GetProperty("paths").GetProperty(Endpoint).GetProperty("get").GetProperty("description").GetString();
        Assert.Contains("不隐藏", description);
        Assert.Contains("duration_complete", description);
    }
}
