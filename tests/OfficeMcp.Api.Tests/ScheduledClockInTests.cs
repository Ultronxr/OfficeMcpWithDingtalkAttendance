using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OfficeMcp.Api.Features.Attendance;
using OfficeMcp.Api.Features.DeviceCommands;
using OfficeMcp.Api.Infrastructure.DingTalk;

namespace OfficeMcp.Api.Tests;

/// <summary>隔离设备与钉钉的端到端协议测试，覆盖事后核验、重传、恢复及远程回归。</summary>
public sealed class ScheduledClockInTests
{
    private const string Endpoint = "/api/devices/office-phone/attendance/executions";
    private static readonly JsonSerializerOptions Json = DeviceCommandStore.JsonOptions;

    /// <summary>构造当天一分钟前完成的合成上班执行，不使用真实员工或手机资料。</summary>
    private static ScheduledClockInReport Report(OfficeApiFactory factory)
    {
        var at = factory.Clock.GetUtcNow().AddMinutes(-1);
        return new(Guid.NewGuid().ToString("N"), at.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd"),
            "OnDuty", at.ToUnixTimeMilliseconds(), "launch_requested", at.AddSeconds(16).ToUnixTimeMilliseconds(),
            at.AddMilliseconds(400).ToUnixTimeMilliseconds(), at.AddSeconds(1).ToUnixTimeMilliseconds());
    }

    /// <summary>通过真实设备认证管线提交回执，解析公开 Task 摘要。</summary>
    private static async Task<ClockInTaskResponse> Submit(HttpClient client, ScheduledClockInReport report)
    {
        var response = await client.PostAsJsonAsync(Endpoint, report, Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ClockInTaskResponse>(Json))!;
    }

    /// <summary>模拟 Worker 读取最新持久化状态执行一轮核验，省去真实等待时间。</summary>
    private static async Task<ClockInTaskResponse> Process(OfficeApiFactory factory, string id)
    {
        var store = factory.Services.GetRequiredService<DeviceCommandStore>();
        var service = factory.Services.GetRequiredService<ClockInService>();
        await service.ProcessAsync((await store.GetAsync(id, default))!, default);
        return await service.GetAsync(id, default);
    }

    /// <summary>生成固定日期的官方考勤响应，可选择异常考勤状态或不存在记录。</summary>
    private static string Snapshot(long? at, string status = "Normal", string type = "OnDuty") =>
        JsonSerializer.Serialize(new { errcode = 0, result = new { userid = OfficeApiFactory.UserId,
            attendance_result_list = at is null ? Array.Empty<object>() : new object[] {
                new { record_id = 1, plan_id = 1, check_type = type, time_result = status, user_check_time = at }
            } } });

    /// <summary>事后首读已含新记录也能成功，晚到上报不能使用 Task 创建时间排除有效记录。</summary>
    [Theory]
    [InlineData("Normal")]
    [InlineData("Late")]
    [InlineData("SeriousLate")]
    public async Task DelayedReportConfirmsExistingMatchingRecord(string status)
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var client = factory.DeviceClient();
        var report = Report(factory);
        factory.Clock.Advance(TimeSpan.FromHours(2));
        factory.Handler.VerificationResponse = _ => Snapshot(report.ExecutedAtUnixMs + 4000, status);
        var task = await Submit(client, report);
        Assert.Equal("verifying", task.State);
        Assert.Equal(0, factory.Handler.VerificationCalls); // 接收只保存事实，不取伪造的事前基线。
        Assert.Equal(factory.Clock.GetUtcNow().AddSeconds(120), task.VerificationDeadline);
        task = await Process(factory, task.TaskId);
        Assert.Equal("succeeded", task.State);
        Assert.True(task.AttendanceConfirmed);
        Assert.Equal("within_execution_window", task.VerificationRelation);
        Assert.Equal(status, task.Record!.StatusCode);
        Assert.Equal(1, task.VerificationAttempts);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(
            "/api/devices/office-phone/commands/lease", new { wait_seconds = 0 })).StatusCode);
        using var api = factory.AuthenticatedClient();
        var publicResult = await api.GetFromJsonAsync<ClockInTaskResponse>("/api/attendance/clock-in/" + task.TaskId, Json);
        Assert.Equal("local_schedule", publicResult!.Source);
        var body = await client.GetStringAsync("/api/devices/office-phone/attendance/tasks/" + task.TaskId);
        Assert.DoesNotContain(OfficeApiFactory.UserId, body);
        Assert.DoesNotContain("lease_token", body);
    }

    /// <summary>提前存在的卡有独立结果，不能称本地动作未执行或把旧卡当成新卡。</summary>
    [Fact]
    public async Task EarlierRecordIsAlreadyCompletedWithAccurateMessage()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var client = factory.DeviceClient();
        var report = Report(factory);
        factory.Handler.VerificationResponse = _ => Snapshot(report.ExecutedAtUnixMs - 31000);
        var task = await Process(factory, (await Submit(client, report)).TaskId);
        Assert.Equal("already_completed", task.State);
        Assert.Equal("before_execution", task.VerificationRelation);
        Assert.True(task.AttendanceConfirmed);
        Assert.Contains("本地子任务已运行", task.Message);
        Assert.DoesNotContain("未再次下发", task.Message);
    }

    /// <summary>30 秒边界可匹配；晚于执行核验窗口的无关记录不会因延迟补报误算成功。</summary>
    [Theory]
    [InlineData(-30000, "succeeded")]
    [InlineData(150000, "succeeded")]
    [InlineData(150001, "verifying")]
    public async Task MatchingWindowIsBoundedByExecutionTime(long difference, string state)
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var client = factory.DeviceClient();
        var report = Report(factory);
        factory.Clock.Advance(TimeSpan.FromHours(1));
        factory.Handler.VerificationResponse = _ => Snapshot(report.ExecutedAtUnixMs + difference);
        Assert.Equal(state, (await Process(factory, (await Submit(client, report)).TaskId)).State);
    }

    /// <summary>没有记录或未知状态不会误判成功；上游延迟可在后续轮询确认，超时保留观察结果。</summary>
    [Fact]
    public async Task PollsUntilRecordAppearsAndKeepsUnknownStatusOnTimeout()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var client = factory.DeviceClient();
        var report = Report(factory);
        factory.Handler.VerificationResponse = count => count == 1 ? Snapshot(null) : Snapshot(report.ExecutedAtUnixMs + 4000);
        var task = await Submit(client, report);
        Assert.Equal("verifying", (await Process(factory, task.TaskId)).State);
        factory.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("succeeded", (await Process(factory, task.TaskId)).State);
        var unknown = await Submit(client, Report(factory));
        factory.Handler.VerificationResponse = _ => Snapshot(report.ExecutedAtUnixMs, "FutureProviderStatus");
        unknown = await Process(factory, unknown.TaskId);
        Assert.False(unknown.AttendanceConfirmed);
        factory.Clock.Advance(TimeSpan.FromSeconds(121));
        unknown = await Process(factory, unknown.TaskId);
        Assert.Equal("unconfirmed", unknown.State);
        Assert.Equal("FutureProviderStatus", unknown.Record!.StatusCode);
        Assert.Equal("unknown_status", unknown.VerificationRelation);
    }

    /// <summary>执行中断或动作失败仍由官方 API 复核，不能直接当作业务失败。</summary>
    [Theory]
    [InlineData("uncertain")]
    [InlineData("failed")]
    [InlineData("busy")]
    [InlineData("expired")]
    public async Task DeviceFailureStillGetsFullVerificationWindow(string outcome)
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var client = factory.DeviceClient();
        var report = Report(factory) with { Outcome = outcome, ErrorCode = "action_interrupted" };
        var task = await Submit(client, report);
        factory.Handler.VerificationResponse = _ => """{"errcode":88}""";
        task = await Process(factory, task.TaskId);
        Assert.Equal("verifying", task.State);
        Assert.Contains("dingtalk_api_error", task.VerificationError);
        factory.Handler.VerificationResponse = _ => Snapshot(null);
        task = await Process(factory, task.TaskId);
        Assert.Equal("verifying", task.State);
        Assert.Null(task.VerificationError);
        factory.Clock.Advance(TimeSpan.FromSeconds(121));
        Assert.Equal("unconfirmed", (await Process(factory, task.TaskId)).State);
    }

    /// <summary>并发重传返回原任务和期限，终态及跨日重传不覆盖结果；篡改事实返回冲突。</summary>
    [Fact]
    public async Task RetriesAreIdempotentAndDoNotExtendDeadline()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var client = factory.DeviceClient();
        var report = Report(factory);
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Submit(client, report)));
        Assert.Single(responses.Select(x => x.TaskId).Distinct());
        factory.Clock.Advance(TimeSpan.FromSeconds(50));
        var retry = await Submit(client, report);
        Assert.Equal(responses[0].VerificationDeadline, retry.VerificationDeadline);
        factory.Handler.VerificationResponse = _ => Snapshot(report.ExecutedAtUnixMs + 4000);
        var finished = await Process(factory, retry.TaskId);
        factory.Clock.Advance(TimeSpan.FromDays(1));
        retry = await Submit(client, report);
        Assert.Equal(finished.FinishedAt, retry.FinishedAt);
        Assert.Equal("succeeded", retry.State);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Endpoint,
            report with { Outcome = "failed" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Endpoint,
            report with { LocalRunId = Guid.NewGuid().ToString("N") }, Json)).StatusCode);
    }

    /// <summary>禁止跨设备读写或使用办公 API Key 绕过设备认证，并校验本地上报参数。</summary>
    [Fact]
    public async Task EnforcesDeviceOwnershipAndTimeValidation()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var client = factory.DeviceClient();
        using var other = factory.DeviceClient("other-phone");
        using var api = factory.AuthenticatedClient();
        var report = Report(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.PostAsJsonAsync(Endpoint, report, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(Endpoint, report, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(
            "/api/devices/other-phone/attendance/executions", report, Json)).StatusCode);
        var task = await Submit(client, report);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(
            "/api/devices/other-phone/attendance/tasks/" + task.TaskId)).StatusCode);
        foreach (var invalid in new[] {
            report with { CheckType = "OffDuty" }, report with { LocalRunId = "bad" },
            report with { ExecutedAtUnixMs = long.MaxValue }, report with { ErrorCode = "raw secret" },
            report with { ScreenOnAtUnixMs = report.ExecutedAtUnixMs - 1 },
            report with { AppRequestedAtUnixMs = null }, report with { WorkDate = "2026-09-10" },
            report with { CompletedAtUnixMs = factory.Clock.GetUtcNow().AddSeconds(31).ToUnixTimeMilliseconds() }
        }) Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Endpoint,
            invalid with { LocalRunId = invalid.LocalRunId == "bad" ? "bad" : Guid.NewGuid().ToString("N") }, Json)).StatusCode);
    }

    /// <summary>本地核验不占用远程动作名额；原领取、凭据校验、幂等及上班基线规则继续生效。</summary>
    [Fact]
    public async Task RemoteCommandsAndVerificationOnlyTasksRemainIsolated()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var device = factory.DeviceClient();
        using var api = factory.AuthenticatedClient();
        var local = await Submit(device, Report(factory));
        var request = new ClockInRequest(Guid.NewGuid().ToString("N"), "OnDuty", WaitSeconds: 0);
        var remoteResponse = await api.PostAsJsonAsync("/api/attendance/clock-in", request, Json);
        Assert.Equal(HttpStatusCode.Accepted, remoteResponse.StatusCode);
        var remote = (await remoteResponse.Content.ReadFromJsonAsync<ClockInTaskResponse>(Json))!;
        Assert.Equal("remote_command", remote.Source);
        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsJsonAsync("/api/attendance/clock-in",
            request with { RequestId = Guid.NewGuid().ToString("N") }, Json)).StatusCode);
        await Submit(device, Report(factory)); // 已有远程动作时仍能记录新的本地事实。
        var leaseResponse = await device.PostAsJsonAsync("/api/devices/office-phone/commands/lease", new { wait_seconds = 0 });
        var lease = (await leaseResponse.Content.ReadFromJsonAsync<DeviceLease>(Json))!;
        Assert.Equal(remote.TaskId, lease.TaskId);
        Assert.Equal(HttpStatusCode.Forbidden, (await device.PostAsJsonAsync(
            $"/api/devices/office-phone/commands/{remote.TaskId}/report", new DeviceReport("wrong", "launch_requested"), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await device.PostAsJsonAsync(
            $"/api/devices/office-phone/commands/{local.TaskId}/report", new DeviceReport(lease.LeaseToken, "launch_requested"), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync(
            $"/api/devices/office-phone/commands/{remote.TaskId}/report", new DeviceReport(lease.LeaseToken, "launch_requested"), Json)).StatusCode);
        factory.Handler.VerificationResponse = _ => Snapshot(factory.Clock.GetUtcNow().ToUnixTimeMilliseconds());
        Assert.Equal("succeeded", (await Process(factory, remote.TaskId)).State);
        var already = await api.PostAsJsonAsync("/api/attendance/clock-in",
            request with { RequestId = Guid.NewGuid().ToString("N") }, Json);
        Assert.Equal("already_completed", (await already.Content.ReadFromJsonAsync<ClockInTaskResponse>(Json))!.State);
    }

    /// <summary>真实磁盘恢复可以继续本地核验，旧 JSON 无来源字段按远程语义读取；纯核验即使误入 queued 也不能领取。</summary>
    [Fact]
    public async Task PersistedTasksRestoreWithoutCreatingDeviceActions()
    {
        await using var factory = new OfficeApiFactory { EnableDevices = true };
        using var device = factory.DeviceClient();
        var report = Report(factory);
        var task = await Submit(device, report);
        var originalStore = factory.Services.GetRequiredService<DeviceCommandStore>();
        var local = (await originalStore.GetAsync(task.TaskId, default))!;
        var directory = Path.Combine(factory.DeviceStateDirectory, "restore");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, local.Id + ".json"), JsonSerializer.Serialize(local, Json));
        using var restored = new DeviceCommandStore(Options.Create(new DeviceCommandOptions { StateDirectory = directory }), factory.Clock);
        Assert.Null(await restored.LeaseAsync("office-phone", default));
        var service = new ClockInService(restored, factory.Services.GetRequiredService<DingTalkClient>(),
            factory.Services.GetRequiredService<IOptions<ClockInOptions>>(), factory.Services.GetRequiredService<IOptions<AttendanceOptions>>(),
            factory.Services.GetRequiredService<IOptions<DeviceCommandOptions>>(), factory.Clock);
        factory.Handler.VerificationResponse = _ => Snapshot(report.ExecutedAtUnixMs + 4000);
        await service.ProcessAsync((await restored.GetAsync(local.Id, default))!, default);
        Assert.Equal("succeeded", (await restored.GetAsync(local.Id, default))!.State);
        await restored.UpdateAsync(local.Id, x => x with { State = "queued", ExpiresAt = factory.Clock.GetUtcNow().AddMinutes(1) }, default);
        Assert.Null(await restored.LeaseAsync("office-phone", default));
        var legacyJson = JsonNode.Parse(JsonSerializer.Serialize(local with { Source = DeviceCommandJob.RemoteCommand }, Json))!;
        legacyJson.AsObject().Remove("source");
        Assert.True(legacyJson.Deserialize<DeviceCommandJob>(Json)!.RequiresDeviceAction);
    }
}
