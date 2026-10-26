using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using OfficeMcp.Api.Features.DeviceCommands;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OfficeMcp.Api.Features.Attendance;

/// <summary>通过独立只读工具探测固定打卡设备，保留原自动开关与 Task 在线状态语义。</summary>
public static class DeviceStatusEndpoints
{
    /// <summary>注册办公身份可用的实时设备往返探测接口。</summary>
    /// <param name="group">已要求办公 API Key 的业务路由组。</param>
    public static RouteGroupBuilder MapDeviceStatusEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/attendance/device-status", GetAsync).WithName("attendance_device_status").WithTags("Attendance")
            .WithSummary("实时探测打卡设备通信是否在线")
            .WithDescription("只读。每次发送独立挑战，收到手机对本次挑战的应答才返回 online=true；不使用最近60秒连接标记。wait_seconds 默认10，范围1至30。online=false/status=probe_timeout 表示期限内未证实可用，可能是网络断开、接收器停止、未升级或忙碌；不能据此确定具体故障。成功仅证明 checked_at 时通信可用，不保证稍后打卡成功。elapsed_ms 包含等待领取时间，不是网络RTT。不亮屏、不启动应用、不创建打卡任务、不修改开关。")
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(503);
        return group;
    }

    /// <summary>每次请求创建独立探测；超时是正常查询结果，客户端取消不保留后台待办。</summary>
    private static async Task<Ok<DeviceProbeResponse>> GetAsync(DeviceProbeService probes, CancellationToken token,
        [FromQuery(Name = "wait_seconds")] int waitSeconds = 10) =>
        TypedResults.Ok(await probes.QueryAsync(waitSeconds, token));
}

/// <summary>只补充在线探测工具的等待范围，复用项目现有 OpenAPI 生成器。</summary>
public sealed class DeviceStatusOperationFilter : IOperationFilter
{
    /// <summary>给新工具设置参数说明与默认值，不修改已有工具契约。</summary>
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (operation.OperationId != "attendance_device_status") return;
        var parameter = operation.Parameters.Single(x => x.Name == "wait_seconds");
        parameter.Description = "探测总等待秒数，默认10，允许1至30；收到本次应答即提前返回。";
        parameter.Schema.Minimum = 1;
        parameter.Schema.Maximum = 30;
        parameter.Schema.Default = new OpenApiInteger(10);
    }
}
