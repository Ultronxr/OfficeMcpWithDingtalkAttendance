using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OfficeMcp.Api.Features.Attendance;
using OfficeMcp.Api.Features.DeviceCommands;
using OfficeMcp.Api.Infrastructure.Time;

namespace OfficeMcp.Api.Tests;

/// <summary>检查实际 JSON 格式、旧状态、参数与日志，不能仅比较 DateTimeOffset 的相等瞬间。</summary>
public sealed class TimeConventionTests
{
    private static readonly JsonSerializerOptions Json = DeviceCommandStore.JsonOptions;
    private const string Expected = "2026-09-25T09:00:00.1234567+08:00";

    /// <summary>解析与存储保持原精度；不同偏移输入对外只显示秒，不重复加八小时。</summary>
    [Theory]
    [InlineData("2026-09-25T01:00:00.1234567Z")]
    [InlineData("2026-09-25T01:00:00.1234567+00:00")]
    [InlineData("2026-09-24T21:00:00.1234567-04:00")]
    [InlineData("2026-09-25T09:00:00.1234567+08:00")]
    [InlineData("2026-09-25 09:00:00.1234567")]
    [InlineData("2026-09-25T09:00:00.1234567")]
    public void NormalizesWithoutChangingInstant(string input)
    {
        var actual = JsonSerializer.Deserialize<DateTimeOffset>(JsonSerializer.Serialize(input), Json);
        Assert.Equal(OfficeTime.Offset, actual.Offset);
        Assert.Equal("2026-09-25T09:00:00+08:00", OfficeTime.Text(actual));
        Assert.Equal(DateTimeOffset.Parse(Expected, CultureInfo.InvariantCulture).Ticks, actual.Ticks);
        Assert.Equal(Expected, JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(actual, Json)));
        Assert.Equal("2026-09-25T09:00:00+08:00",
            JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(actual, OfficeTime.CreateJsonOptions())));
    }

    /// <summary>已知时间字段无效必须报错，不能借用系统区域设置或悄悄进位日期。</summary>
    [Theory]
    [InlineData("2026-02-30 09:00:00")]
    [InlineData("09/25/2026 09:00")]
    [InlineData("2026-09-25T09:00:00+25:00")]
    [InlineData("9999-12-31T23:59:59Z")]
    public void RejectsInvalidTimes(string input) => Assert.Throws<JsonException>(() => OfficeTime.Parse(input));

    /// <summary>全量明细支持数字、字符串、无偏移和有偏移时间；记录 ID 与未知数据不变。</summary>
    [Fact]
    public async Task AttendanceAndOvertimeUseSameReadableDetails()
    {
        await using var factory = new OfficeApiFactory();
        var actualMs = OfficeTime.Parse("2026-09-10T21:30:00+08:00").ToUnixTimeMilliseconds();
        factory.Handler.AttendanceResponse = _ => $$"""
            {"errcode":0,"recordresult":[{"id":1790006400000,"userId":"test-user-000001","checkType":"OffDuty",
            "workDate":"2026-09-10 00:00:00","planCheckTime":"2026-09-10T10:00:00Z","baseCheckTime":0,
            "userCheckTime":{{actualMs}},"gmtCreate":"{{actualMs}}","gmtModified":"2026-09-10T21:30:00.1234567+08:00",
            "timeResult":"Normal","timeZone":"Asia/Shanghai","futureField":{"id":{{actualMs}},"text":"unchanged"},
            "nested":[{"gmt_create":"2026-09-10T13:30:00Z"}]}]}
            """;
        using var client = factory.AuthenticatedClient();
        foreach (var endpoint in new[] { "attendance", "attendance/overtime" })
        {
            using var doc = JsonDocument.Parse(await client.GetStringAsync(
                $"/api/{endpoint}?start_date=2026-09-10&end_date=2026-09-10&detail=full"));
            var root = doc.RootElement;
            var day = endpoint == "attendance" ? root[0] : root.GetProperty("days")[0];
            if (endpoint != "attendance")
            {
                Assert.False(root.TryGetProperty("time_zone", out _));
                Assert.Equal("09:00:00+08:00", root.GetProperty("rule").GetProperty("work_start_time").GetString());
                Assert.Equal(3.5m, root.GetProperty("summary").GetProperty("total_overtime_hours").GetDecimal());
            }
            Assert.False(day.TryGetProperty("time_zone", out _));
            var record = day.GetProperty("records")[0];
            var details = record.GetProperty("details");
            Assert.Equal("2026-09-10T21:30:00+08:00", record.GetProperty("actual_check_time").GetString());
            Assert.Equal("2026-09-10T00:00:00+08:00", details.GetProperty("workDate").GetString());
            Assert.Equal("2026-09-10T18:00:00+08:00", details.GetProperty("planCheckTime").GetString());
            Assert.Equal(JsonValueKind.Null, details.GetProperty("baseCheckTime").ValueKind);
            foreach (var key in new[] { "userCheckTime", "gmtCreate" })
                Assert.Equal("2026-09-10T21:30:00+08:00", details.GetProperty(key).GetString());
            Assert.Equal("2026-09-10T21:30:00+08:00", details.GetProperty("gmtModified").GetString());
            Assert.Equal("2026-09-10T21:30:00+08:00", details.GetProperty("nested")[0].GetProperty("gmt_create").GetString());
            Assert.Equal(1790006400000, details.GetProperty("id").GetInt64());
            Assert.Equal(actualMs, details.GetProperty("futureField").GetProperty("id").GetInt64());
            Assert.False(details.TryGetProperty("timeZone", out _));
        }
    }

    /// <summary>新增的上游创建时间即使不参与摘要，也不能把无效值原样泄漏成成功明细。</summary>
    [Fact]
    public async Task InvalidDetailTimeUsesExistingSegmentFailure()
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponse = _ => """
            {"errcode":0,"recordresult":[{"userId":"test-user-000001","workDate":"2026-09-10 00:00:00","gmtCreate":"bad"}]}
            """;
        using var client = factory.AuthenticatedClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/attendance?start_date=2026-09-10&end_date=2026-09-10&detail=full"));
        Assert.False(doc.RootElement[0].GetProperty("success").GetBoolean());
        Assert.Equal("dingtalk_invalid_response", doc.RootElement[0].GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>主动任务整个生命周期与存盘使用 +08:00，设备领取仍使用未平移的数值期限。</summary>
    [Fact]
    public async Task RemoteLifecycleAndDeviceProtocolHaveCorrectTimeRepresentations()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var api = factory.AuthenticatedClient();
        using var phone = factory.DeviceClient();
        // UTC 仍是前一天，而北京时间已过午夜；默认 work_date 必须选北京时间今天。
        factory.Clock.Advance(TimeSpan.FromHours(-7));
        var created = await api.PostAsJsonAsync("/api/attendance/clock-in", new ClockInRequest(Guid.NewGuid().ToString(), "OnDuty", WaitSeconds: 0), Json);
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        using var first = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = first.RootElement.GetProperty("task_id").GetString()!;
        Assert.Equal("2026-09-11", first.RootElement.GetProperty("work_date").GetString());
        AssertTime(first.RootElement, "created_at", factory.Clock.GetUtcNow());
        AssertTime(first.RootElement, "command_expires_at", factory.Clock.GetUtcNow().AddSeconds(120));
        var leased = await phone.PostAsJsonAsync("/api/devices/office-phone/commands/lease", new { wait_seconds = 0 });
        var lease = (await leased.Content.ReadFromJsonAsync<DeviceLease>(Json))!;
        Assert.Equal(factory.Clock.GetUtcNow().ToUnixTimeMilliseconds(), lease.ServerTimeUnixMs);
        Assert.Equal(factory.Clock.GetUtcNow().AddSeconds(120).ToUnixTimeMilliseconds(), lease.ExpiresAtUnixMs);
        await phone.PostAsJsonAsync($"/api/devices/office-phone/commands/{id}/report", new DeviceReport(lease.LeaseToken, "launch_requested"), Json);
        var store = factory.Services.GetRequiredService<DeviceCommandStore>();
        var service = factory.Services.GetRequiredService<ClockInService>();
        var at = factory.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        factory.Handler.VerificationResponse = _ => JsonSerializer.Serialize(new { errcode = 0,
            result = new { userid = OfficeApiFactory.UserId, attendance_result_list = new[] {
                new { check_type = "OnDuty", time_result = "Normal", user_check_time = at } } } });
        await service.ProcessAsync((await store.GetAsync(id, default))!, default);
        foreach (var endpoint in new[] { $"/api/attendance/clock-in/{id}", $"/api/devices/office-phone/attendance/tasks/{id}" })
        {
            using var result = JsonDocument.Parse(await (endpoint.StartsWith("/api/devices") ? phone : api).GetStringAsync(endpoint));
            Assert.True(result.RootElement.GetProperty("is_terminal").GetBoolean());
            foreach (var field in new[] { "finished_at", "last_verified_at" }) AssertTime(result.RootElement, field, factory.Clock.GetUtcNow());
        }
        using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(factory.DeviceStateDirectory, id + ".json")));
        AssertTime(persisted.RootElement, "claimed_at", factory.Clock.GetUtcNow(), stored: true);
        AssertTime(persisted.RootElement, "verification_deadline", factory.Clock.GetUtcNow().AddSeconds(120), stored: true);
    }

    /// <summary>被动上报保持原始数值与幂等；公开 Task 用可读字段，避免 Agent 处理 Unix 毫秒。</summary>
    [Fact]
    public async Task LocalExecutionHasReadableNamesWithoutChangingReport()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var phone = factory.DeviceClient();
        var at = factory.Clock.GetUtcNow().AddSeconds(-20);
        var report = new ScheduledClockInReport(Guid.NewGuid().ToString("N"), "2026-09-11", "OnDuty",
            at.ToUnixTimeMilliseconds(), "launch_requested", at.AddSeconds(16).ToUnixTimeMilliseconds(),
            at.AddMilliseconds(400).ToUnixTimeMilliseconds(), at.AddSeconds(1).ToUnixTimeMilliseconds());
        var response = await phone.PostAsJsonAsync("/api/devices/office-phone/attendance/executions", report, Json);
        response.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var execution = result.RootElement.GetProperty("local_execution");
        AssertTime(execution, "executed_at", at);
        AssertTime(execution, "completed_at", at.AddSeconds(16));
        AssertTime(execution, "screen_on_at", at.AddMilliseconds(400));
        AssertTime(execution, "app_requested_at", at.AddSeconds(1));
        Assert.DoesNotContain("_unix_ms", execution.GetRawText());
        var again = await phone.PostAsJsonAsync("/api/devices/office-phone/attendance/executions", report, Json);
        using var repeat = JsonDocument.Parse(await again.Content.ReadAsStringAsync());
        Assert.Equal(result.RootElement.GetProperty("task_id").GetString(), repeat.RootElement.GetProperty("task_id").GetString());
    }

    /// <summary>开关设置、查询和设备确认共用格式，状态文件也使用相同序列化。</summary>
    [Fact]
    public async Task AutomationResponsesAndStorageAreReadable()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var api = factory.AuthenticatedClient();
        using var phone = factory.DeviceClient();
        var response = await api.PostAsJsonAsync("/api/attendance/automation", new AutomationRequest(Guid.NewGuid().ToString(), false, 0), Json);
        using var set = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertTime(set.RootElement, "updated_at", factory.Clock.GetUtcNow());
        await phone.PostAsJsonAsync("/api/devices/office-phone/attendance/automation/sync", new AutomationSyncRequest(1, false), Json);
        using var status = JsonDocument.Parse(await api.GetStringAsync("/api/attendance/automation"));
        AssertTime(status.RootElement, "applied_at", factory.Clock.GetUtcNow());
        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(factory.DeviceStateDirectory, "automation", "state.json")));
        AssertTime(state.RootElement, "updated_at", factory.Clock.GetUtcNow(), stored: true);
        AssertTime(state.RootElement, "applied_at", factory.Clock.GetUtcNow(), stored: true);
    }

    /// <summary>旧 +00:00 文件仍可读取，更新时写入 +08:00，过期时刻毫秒值不变。</summary>
    [Fact]
    public async Task LegacyUtcStateLoadsAndRewritesWithoutShiftingDeadline()
    {
        var directory = Path.Combine(Path.GetTempPath(), "office-time-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var clock = new TestClock();
            clock.Advance(TimeSpan.FromTicks(1234567));
            var id = Guid.NewGuid().ToString("N");
            var job = new DeviceCommandJob { Id = id, RequestId = id, Fingerprint = "synthetic", DeviceId = "test-phone",
                Kind = "synthetic", Action = "synthetic", CreatedAt = clock.GetUtcNow(), ExpiresAt = clock.GetUtcNow().AddSeconds(120),
                Payload = JsonSerializer.SerializeToElement(new {}), Metadata = JsonSerializer.SerializeToElement(new {}) };
            var oldJson = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            await File.WriteAllTextAsync(Path.Combine(directory, id + ".json"), JsonSerializer.Serialize(job, oldJson));
            using var store = new DeviceCommandStore(Options.Create(new DeviceCommandOptions { StateDirectory = directory }), clock);
            Assert.Equal(job.ExpiresAt, (await store.GetAsync(id, default))!.ExpiresAt);
            await store.UpdateAsync(id, value => value with { State = "expired", FinishedAt = clock.GetUtcNow() }, default);
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, id + ".json")));
            AssertTime(saved.RootElement, "expires_at", job.ExpiresAt, stored: true);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>日志时间与结构化日期值共享格式，实际宿主注册的也是此 formatter。</summary>
    [Fact]
    public async Task LogAndOpenApiUseSharedConvention()
    {
        await using var factory = new OfficeApiFactory();
        using var client = factory.AuthenticatedClient();
        using var spec = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        foreach (var path in spec.RootElement.GetProperty("paths").EnumerateObject())
            foreach (var operation in path.Value.EnumerateObject())
                if (operation.Value.TryGetProperty("operationId", out _))
                    Assert.Contains(OfficeTime.Contract, operation.Value.GetProperty("description").GetString());
        var schemas = spec.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.False(schemas.GetProperty("AttendanceResponse").GetProperty("properties").TryGetProperty("time_zone", out _));
        var field = schemas.GetProperty("ClockInTaskResponse").GetProperty("properties").GetProperty("created_at");
        Assert.Equal("date-time", field.GetProperty("format").GetString());
        Assert.Equal("2026-09-25T09:00:00+08:00", field.GetProperty("example").GetString());
        var local = schemas.GetProperty("LocalExecutionResponse").GetProperty("properties");
        Assert.True(local.TryGetProperty("executed_at", out _));
        Assert.False(local.TryGetProperty("executed_at_unix_ms", out _));
        var formatter = Assert.Single(factory.Services.GetServices<ConsoleFormatter>().OfType<BeijingJsonConsoleFormatter>());
        var state = new Dictionary<string, object?> { ["At"] = factory.Clock.GetUtcNow() };
        var entry = new LogEntry<Dictionary<string, object?>>(LogLevel.Information, "synthetic", new EventId(1), state, null, (_, _) => "合成日志");
        using var writer = new StringWriter();
        formatter.Write(in entry, null, writer);
        using var log = JsonDocument.Parse(writer.ToString());
        AssertTime(log.RootElement, "Timestamp", factory.Clock.GetUtcNow());
        AssertTime(log.RootElement.GetProperty("State"), "At", factory.Clock.GetUtcNow());
    }

    /// <summary>公开时间省略小数，内部状态保留完整精度；两者都固定为北京时间。</summary>
    private static void AssertTime(JsonElement value, string field, DateTimeOffset instant, bool stored = false)
    {
        var text = value.GetProperty(field).GetString()!;
        Assert.Matches(stored ? @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}\+08:00$" :
            @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\+08:00$", text);
        var expected = stored ? instant : instant.AddTicks(-(instant.Ticks % TimeSpan.TicksPerSecond));
        Assert.Equal(expected, DateTimeOffset.ParseExact(text, stored ? OfficeTime.StorageFormat : OfficeTime.Format, CultureInfo.InvariantCulture));
    }

    /// <summary>展示省略子秒不能四舍五入越过午夜、核验截止或加班门槛。</summary>
    [Fact]
    public void SecondsDisplayDoesNotRoundAcrossBoundary()
    {
        var value = OfficeTime.Parse("2026-09-25T23:59:59.9999999+08:00");
        Assert.Equal("2026-09-25T23:59:59+08:00", OfficeTime.Text(value));
        Assert.Equal("2026-09-25T23:59:59.9999999+08:00", OfficeTime.Text(value, preservePrecision: true));
    }

    /// <summary>读取或重复确认旧开关不重写 UTC 文件；只有新设置才写入北京时间。</summary>
    [Fact]
    public async Task LegacyAutomationReadDoesNotRewriteFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "office-time-automation-tests-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(directory, "automation");
        Directory.CreateDirectory(folder);
        try
        {
            var clock = new TestClock();
            clock.Advance(TimeSpan.FromTicks(1234567));
            var state = new AutomationDocument { Version = 1, DeviceId = "office-phone", Enabled = false,
                Revision = 1, LastDisabledRevision = 1, UpdatedAt = clock.GetUtcNow(), AppliedAt = clock.GetUtcNow(),
                AppliedRevision = 1, AppliedEnabled = false,
                Requests = new() { [Guid.NewGuid().ToString("N")] = new AutomationChange(false, 1) } };
            var oldJson = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            var path = Path.Combine(folder, "state.json");
            var original = JsonSerializer.Serialize(state, oldJson);
            await File.WriteAllTextAsync(path, original);
            using var store = new AutomationStore(Options.Create(new DeviceCommandOptions { Enabled = true, StateDirectory = directory,
                    Devices = new() { ["office-phone"] = new DeviceRegistration { Key = "synthetic-only" } } }),
                Options.Create(new ClockInOptions()), clock);
            Assert.False((await store.GetAsync(default)).Enabled);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            await store.SyncAsync("office-phone", new AutomationSyncRequest(1, false), default);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            await store.SetAsync(Guid.NewGuid().ToString("N"), false, default);
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            AssertTime(saved.RootElement, "updated_at", clock.GetUtcNow(), stored: true);
            Assert.False(saved.RootElement.GetProperty("enabled").GetBoolean());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
