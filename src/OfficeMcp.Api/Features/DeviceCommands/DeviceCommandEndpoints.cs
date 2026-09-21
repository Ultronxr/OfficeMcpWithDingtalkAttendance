using System.Diagnostics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OfficeMcp.Api.Features.Attendance;
using OfficeMcp.Api.Infrastructure.Errors;

namespace OfficeMcp.Api.Features.DeviceCommands;

/// <summary>注册通用设备通道及远程考勤流程，设备端路由不进入网关工具文档。</summary>
public static class DeviceCommandEndpoints
{
    /// <summary>注册任务持久化、独立设备认证和后台核验。</summary>
    public static IServiceCollection AddRemoteCommands(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DeviceCommandOptions>().Bind(configuration.GetSection("RemoteDevices"))
            .Validate(x => !string.IsNullOrWhiteSpace(x.StateDirectory), "RemoteDevices:StateDirectory 不能为空。")
            .Validate(x => !x.Enabled || x.Devices.Count > 0, "启用远程功能前请登记至少一个设备。")
            .Validate(x => x.Devices.All(d => System.Text.RegularExpressions.Regex.IsMatch(d.Key, "^[a-zA-Z0-9_-]{1,64}$")
                && !string.IsNullOrWhiteSpace(d.Value.Key) && d.Value.Key.Length is >= 32 and <= 512), "设备 ID 或设备密钥格式无效。")
            .ValidateOnStart();
        services.AddOptions<ClockInOptions>().Bind(configuration.GetSection("ClockIn"))
            .Validate(x => !string.IsNullOrWhiteSpace(x.DeviceId) && x.CommandTtlSeconds is >= 15 and <= 600
                && x.ExecutionTimeoutSeconds is >= 15 and <= 300 && x.VerificationTimeoutSeconds is >= 15 and <= 600
                && x.VerificationIntervalSeconds is >= 2 and <= 30, "ClockIn 设备或超时配置无效。")
            .ValidateOnStart();
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, DeviceAuthenticationHandler>(DeviceAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization(options => options.AddPolicy(DeviceAuthenticationHandler.PolicyName, policy =>
            policy.AddAuthenticationSchemes(DeviceAuthenticationHandler.SchemeName).RequireAuthenticatedUser().RequireClaim("device_id")));
        services.AddSingleton<DeviceCommandStore>();
        services.AddSingleton<ClockInService>();
        services.AddHostedService<ClockInWorker>();
        services.AddSwaggerGen(options => options.SchemaFilter<ClockInSchemaFilter>());
        return services;
    }

    /// <summary>映射只有手机设备身份可访问的领取和回执接口。</summary>
    public static IEndpointRouteBuilder MapDeviceCommands(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/devices/{deviceId}/commands")
            .RequireAuthorization(DeviceAuthenticationHandler.PolicyName);
        group.MapPost("/lease", LeaseAsync).ExcludeFromDescription();
        group.MapPost("/{taskId}/report", ReportAsync).ExcludeFromDescription();
        var attendance = endpoints.MapGroup("/api/devices/{deviceId}/attendance")
            .RequireAuthorization(DeviceAuthenticationHandler.PolicyName);
        attendance.MapPost("/executions", ReportLocalAsync).ExcludeFromDescription();
        attendance.MapGet("/tasks/{taskId}", GetDeviceTaskAsync).ExcludeFromDescription();
        attendance.MapPost("/automation/sync", SyncAutomationAsync).ExcludeFromDescription();
        return endpoints;
    }

    /// <summary>固定手机同步自动打卡策略；确认只表示本地开关已落盘，不代表考勤成功。</summary>
    private static async Task<Ok<AutomationPolicy>> SyncAutomationAsync(string deviceId,
        [FromBody] AutomationSyncRequest request, HttpContext context, AutomationStore automation,
        DeviceCommandStore devices, CancellationToken token)
    {
        CheckDevice(context, deviceId);
        var result = await automation.SyncAsync(deviceId, request, token);
        await devices.TouchAsync(deviceId, token);
        return TypedResults.Ok(result);
    }

    /// <summary>设备上报已发生的本地执行，只登记核验任务，不生成可领取的动作。</summary>
    private static async Task<Ok<ClockInTaskResponse>> ReportLocalAsync(string deviceId,
        [FromBody] ScheduledClockInReport report, HttpContext context, ClockInService service,
        DeviceCommandStore store, CancellationToken token)
    {
        CheckDevice(context, deviceId);
        await store.TouchAsync(deviceId, token);
        return TypedResults.Ok(await service.ReportLocalAsync(deviceId, report, token));
    }

    /// <summary>常驻接收器拉取本设备最终结果，用于写回手机日志。</summary>
    private static async Task<Ok<ClockInTaskResponse>> GetDeviceTaskAsync(string deviceId, string taskId,
        HttpContext context, ClockInService service, DeviceCommandStore store, CancellationToken token)
    {
        CheckDevice(context, deviceId);
        await store.TouchAsync(deviceId, token);
        return TypedResults.Ok(await service.GetDeviceTaskAsync(deviceId, taskId, token));
    }

    /// <summary>长轮询领取一条命令；没有任务返回 204，已领取任务不会重发。</summary>
    private static async Task<Results<Ok<DeviceLease>, NoContent>> LeaseAsync(string deviceId,
        [FromBody] DeviceLeaseRequest request, HttpContext context, DeviceCommandStore store,
        TimeProvider clock, CancellationToken token)
    {
        CheckDevice(context, deviceId);
        if (request.WaitSeconds is < 0 or > 25) throw new ApiRequestException(400, "invalid_wait_seconds", "领取等待时间为 0 至 25 秒。");
        await store.TouchAsync(deviceId, token);
        var elapsed = Stopwatch.StartNew();
        do
        {
            var job = await store.LeaseAsync(deviceId, token);
            if (job is not null) return TypedResults.Ok(new DeviceLease(job.Id, job.Action, job.LeaseToken!,
                clock.GetUtcNow().ToUnixTimeMilliseconds(), job.ExpiresAt.ToUnixTimeMilliseconds(), job.Payload));
            if (elapsed.Elapsed.TotalSeconds >= request.WaitSeconds) break;
            await Task.Delay(500, token);
        } while (true);
        return TypedResults.NoContent();
    }

    /// <summary>保存设备动作回执，幂等回报不会再次操作手机或延长核验期限。</summary>
    private static async Task<Ok<object>> ReportAsync(string deviceId, string taskId, [FromBody] DeviceReport report,
        HttpContext context, DeviceCommandStore store, TimeProvider clock, CancellationToken token)
    {
        CheckDevice(context, deviceId);
        if (report.Outcome is not ("launch_requested" or "failed" or "uncertain" or "expired" or "busy"))
            throw new ApiRequestException(400, "invalid_device_outcome", "设备动作结果无效。");
        if (report.ErrorCode is not null && !System.Text.RegularExpressions.Regex.IsMatch(report.ErrorCode, "^[a-z_]{1,64}$"))
            throw new ApiRequestException(400, "invalid_device_error", "设备错误码无效。");
        await store.UpdateAsync(taskId, current =>
        {
            if (current.DeviceId != deviceId || current.LeaseToken is null || current.LeaseToken != report.LeaseToken)
                throw new ApiRequestException(403, "invalid_command_lease", "任务领取凭据不匹配。");
            if (current.IsTerminal || current.DeviceOutcome is not null) return current;
            var deadline = clock.GetUtcNow().AddSeconds(current.VerificationTimeoutSeconds);
            return current with
            {
                State = "verifying", DeviceOutcome = report.Outcome, DeviceError = report.ErrorCode,
                VerificationDeadline = deadline < current.VerificationDeadline!.Value ? deadline : current.VerificationDeadline
            };
        }, token);
        return TypedResults.Ok<object>(new { accepted = true });
    }

    /// <summary>防止设备凭据用于领取或回报另一台设备的命令。</summary>
    private static void CheckDevice(HttpContext context, string deviceId)
    {
        if (context.User.FindFirst("device_id")?.Value != deviceId)
            throw new ApiRequestException(403, "device_forbidden", "不能访问其他设备的命令。");
    }
}
