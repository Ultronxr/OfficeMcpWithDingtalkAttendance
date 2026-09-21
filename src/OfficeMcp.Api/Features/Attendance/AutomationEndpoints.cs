using System.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using OfficeMcp.Api.Features.DeviceCommands;
using OfficeMcp.Api.Infrastructure.Errors;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OfficeMcp.Api.Features.Attendance;

/// <summary>远程开关被动自动打卡；设置和只读状态分别提供 MCP 工具，不创建打卡任务。</summary>
public static class AutomationEndpoints
{
    /// <summary>注册动态控制状态与 OpenAPI 请求约束。</summary>
    public static IServiceCollection AddAutomationControl(this IServiceCollection services)
    {
        services.AddSingleton<AutomationStore>();
        services.AddSwaggerGen(options => options.SchemaFilter<AutomationSchemaFilter>());
        return services;
    }

    /// <summary>映射办公身份可用的控制工具，手机同步接口通过独立设备认证映射。</summary>
    public static RouteGroupBuilder MapAutomationEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/attendance/automation", GetAsync).WithName("attendance_automation_status").WithTags("Attendance")
            .WithSummary("查询被动自动打卡开关和手机生效状态")
            .WithDescription("只读。desired_enabled 为服务端设置；sync_state=applied 表示手机已确认当前 revision，pending 表示尚未确认。device_online 仅反映最近连接。关闭不影响主动打卡；开启等下一次原定主任务，不补打。")
            .ProducesProblem(401).ProducesProblem(503);
        group.MapPost("/attendance/automation", SetAsync).WithName("attendance_automation_set").WithTags("Attendance")
            .WithSummary("开启或关闭被动自动打卡")
            .WithDescription("仅在用户明确要求开关自动打卡时调用。enabled=false 禁止主任务规划并取消未执行子任务；enabled=true 从下次主任务恢复，不立即打卡或补计划。主动打卡不受影响。request_id 使用 UUID，重试复用。手机断网沿用上次状态，只有 sync_state=applied 才能宣称当前设置已在手机生效；否则查询 attendance_automation_status，不重复设置。已开始动作不能撤回。request_superseded=true 表示本次请求已被后续设置覆盖，不代表本次设置仍有效。")
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(409).ProducesProblem(503);
        return group;
    }

    /// <summary>组合当前期望版本、手机确认和现有设备心跳；不访问钉钉 API。</summary>
    private static async Task<AutomationResponse> SnapshotAsync(AutomationStore store, DeviceCommandStore devices,
        long? requestRevision, CancellationToken token)
    {
        var value = await store.GetAsync(token);
        return new(value.DeviceId, value.Enabled, value.Revision, value.LastDisabledRevision, value.UpdatedAt,
            value.AppliedEnabled, value.AppliedRevision, value.AppliedAt,
            value.AppliedRevision == value.Revision && value.AppliedEnabled == value.Enabled ? "applied" : "pending",
            await devices.IsOnlineAsync(value.DeviceId, token), requestRevision,
            requestRevision is not null && requestRevision != value.Revision);
    }

    /// <summary>只读取开关；不修改版本，不唤醒手机。</summary>
    private static async Task<Ok<AutomationResponse>> GetAsync(AutomationStore store, DeviceCommandStore devices,
        CancellationToken token) => TypedResults.Ok(await SnapshotAsync(store, devices, null, token));

    /// <summary>保存幂等设置后短暂等待手机确认；断开 HTTP 不撤销已经落盘的设置。</summary>
    private static async Task<Results<Ok<AutomationResponse>, Accepted<AutomationResponse>>> SetAsync(
        [FromBody] AutomationRequest request, AutomationStore store, DeviceCommandStore devices, CancellationToken token)
    {
        if (!Guid.TryParseExact(request.RequestId, "D", out var id))
            throw new ApiRequestException(400, "invalid_request_id", "request_id 必须为带连字符的 UUID。");
        if (request.Enabled is null) throw new ApiRequestException(400, "invalid_enabled", "必须明确提供布尔值 enabled。");
        if (request.WaitSeconds is < 0 or > 60) throw new ApiRequestException(400, "invalid_wait_seconds", "等待时间为 0 至 60 秒。");
        var revision = await store.SetAsync(id.ToString("N"), request.Enabled.Value, token);
        var elapsed = Stopwatch.StartNew();
        AutomationResponse result;
        do
        {
            result = await SnapshotAsync(store, devices, revision, token);
            if (result.SyncState == "applied" || result.RequestSuperseded || elapsed.Elapsed.TotalSeconds >= request.WaitSeconds) break;
            await Task.Delay(250, token);
        } while (true);
        return result.SyncState == "applied" ? TypedResults.Ok(result)
            : TypedResults.Accepted("/api/attendance/automation", result);
    }
}

/// <summary>为 MCP 请求补齐必填参数与范围，避免缺省 enabled 被误解成关闭。</summary>
public sealed class AutomationSchemaFilter : ISchemaFilter
{
    /// <summary>只补充开关请求模型，不修改其他业务协议。</summary>
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type != typeof(AutomationRequest)) return;
        schema.Required.UnionWith(["request_id", "enabled"]);
        schema.Properties["request_id"].Format = "uuid";
        schema.Properties["request_id"].Nullable = false;
        schema.Properties["request_id"].Description = "每次新设置使用新 UUID；相同请求重试复用，避免旧重试覆盖后续设置。";
        schema.Properties["enabled"].Nullable = false;
        schema.Properties["enabled"].Description = "true 开启被动自动打卡；false 关闭。主动打卡不受影响。";
        schema.Properties["wait_seconds"].Minimum = 0;
        schema.Properties["wait_seconds"].Maximum = 60;
        schema.Properties["wait_seconds"].Default = new OpenApiInteger(45);
    }
}
