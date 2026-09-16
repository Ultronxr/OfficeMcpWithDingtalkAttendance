using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OfficeMcp.Api.Features.Attendance;

/// <summary>网关可见的远程打卡操作和只读状态查询。</summary>
public static class ClockInEndpoints
{
    /// <summary>注册有副作用的 POST 工具；状态查询保持 GET，且不暴露设备密钥接口。</summary>
    public static RouteGroupBuilder MapClockInEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/attendance/clock-in", CreateAsync).WithName("attendance_clock_in").WithTags("Attendance")
            .WithSummary("请求手机现在打卡，并等待官方结果核验")
            .WithDescription("仅在用户明确要求实际打卡时调用。手机亮屏并拉起钉钉，依赖已配置的自动打卡。check_type 为 OnDuty 或 OffDuty；上班已有记录时不重发，下班需核验时间更新。request_id 为 UUID，重试同一操作必须复用。默认等待45秒，未完成返回task_id，后台继续核验；只有attendance_confirmed=true才确认有记录。")
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(409).ProducesProblem(502).ProducesProblem(503).ProducesProblem(504);
        group.MapGet("/attendance/clock-in/{task_id}", GetAsync).WithName("attendance_clock_in_status").WithTags("Attendance")
            .WithSummary("查询远程打卡任务结果")
            .WithDescription("按 task_id 查询已创建任务，不产生新的手机动作。结果未确认时不要重新创建重复任务。")
            .ProducesProblem(401).ProducesProblem(404).ProducesProblem(503);
        return group;
    }

    /// <summary>创建任务后短暂等待；仍在后台处理时返回 202 和状态查询地址。</summary>
    private static async Task<Results<Ok<ClockInTaskResponse>, Accepted<ClockInTaskResponse>>> CreateAsync(
        [FromBody] ClockInRequest request, ClockInService service, CancellationToken token)
    {
        var result = await service.CreateAsync(request, token);
        return result.IsTerminal ? TypedResults.Ok(result)
            : TypedResults.Accepted($"/api/attendance/clock-in/{result.TaskId}", result);
    }

    /// <summary>读取任务状态，返回脱敏用户信息和已核验的考勤记录。</summary>
    private static async Task<Ok<ClockInTaskResponse>> GetAsync([FromRoute(Name = "task_id")] string taskId, ClockInService service,
        CancellationToken token) => TypedResults.Ok(await service.GetAsync(taskId, token));
}

/// <summary>为远程任务的请求体补全网关所需的必填项、枚举与等待范围。</summary>
public sealed class ClockInSchemaFilter : ISchemaFilter
{
    /// <summary>仅修饰远程打卡请求模型，不改变现有考勤查询或设备协议。</summary>
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type != typeof(ClockInRequest)) return;
        schema.Required.Add("request_id");
        schema.Required.Add("check_type");
        schema.Properties["request_id"].Format = "uuid";
        schema.Properties["request_id"].Description = "新操作使用新 UUID；重试同一操作必须复用。";
        schema.Properties["check_type"].Enum = [new OpenApiString("OnDuty"), new OpenApiString("OffDuty")];
        schema.Properties["work_date"].Format = "date";
        schema.Properties["work_date"].Description = "默认北京时间今天；前一天只支持明确的跨午夜下班排班。";
        schema.Properties["wait_seconds"].Minimum = 0;
        schema.Properties["wait_seconds"].Maximum = 60;
        schema.Properties["wait_seconds"].Default = new OpenApiInteger(45);
    }
}
