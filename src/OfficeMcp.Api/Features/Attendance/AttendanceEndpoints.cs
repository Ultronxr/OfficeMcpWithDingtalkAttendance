using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Any;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OfficeMcp.Api.Features.Attendance;

/// <summary>考勤功能的依赖注册和 HTTP 接口，后续功能可在 Features 下并列添加。</summary>
public static class AttendanceEndpoints
{
    /// <summary>注册默认员工配置和考勤业务服务。</summary>
    public static IServiceCollection AddAttendanceFeature(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AttendanceOptions>().Bind(configuration.GetSection("Attendance"))
            .Validate(x => !string.IsNullOrWhiteSpace(x.UserId), "Attendance:UserId 不能为空。")
            .Validate(x => x.MaxQueryDays is >= 1 and <= 366, "Attendance:MaxQueryDays 必须在 1 至 366 之间。")
            .Validate(x => x.MaxParallelDays is >= 1 and <= 8, "Attendance:MaxParallelDays 必须在 1 至 8 之间。")
            .Validate(x => x.QueryTimeoutSeconds is >= 1 and <= 210, "Attendance:QueryTimeoutSeconds 必须在 1 至 210 秒之间。")
            .ValidateOnStart();
        services.AddScoped<AttendanceService>();
        services.AddOptions<OvertimeOptions>().Bind(configuration.GetSection("Overtime"))
            .Validate(x => x.WorkStartTime < x.WorkEndTime && x.WorkEndTime <= x.ThresholdTime,
                "Overtime 时间必须满足同一天内 WorkStartTime < WorkEndTime <= ThresholdTime。")
            .ValidateOnStart();
        services.AddScoped<OvertimeService>();
        services.AddSwaggerGen(options => options.OperationFilter<AttendanceDateOperationFilter>());
        return services;
    }

    /// <summary>注册只读的日期范围查询接口，并固定网关使用的 operationId。</summary>
    public static RouteGroupBuilder MapAttendanceEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/attendance", QueryAsync)
            .WithName("attendance_query").WithTags("Attendance")
            .WithSummary("查询员工日期范围内的打卡明细")
            .WithDescription("start_date 和 end_date 必填，格式 yyyy-MM-dd，包含首尾日期，自动按最多七天分段。user_id、user_name 可选其一，都不填使用默认员工；姓名精确匹配，重名返回候选 ID。detail 默认 simple，full 附上完整明细且已知时间统一为可读北京时间。结果按工作日升序，失败段的日期 success=false，其他段仍返回。摘要时间为北京时间。")
            .ProducesProblem(401).ProducesProblem(404).ProducesProblem(409)
            .ProducesProblem(500).ProducesProblem(502).ProducesProblem(504);
        group.MapGet("/attendance/overtime", QueryOvertimeAsync)
            .WithName("attendance_overtime").WithTags("Attendance")
            .WithSummary("查询日期范围内的加班日考勤与加班统计")
            .WithDescription("复用考勤查询的日期、员工及 detail 参数。按服务端固定作息配置判断，不使用钉钉计划时间；有实际 OffDuty 下班卡才参与，每个工作日取最晚一次，达到门槛（含）即计加班，时长从配置的正常下班时间开始。跨午夜按原 work_date 归属。days 仅含加班日并保留当天全部打卡记录；summary 汇总成功日期，complete=false 时必须同时查看 errors，不得把查询失败当成未加班。")
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409)
            .ProducesProblem(500).ProducesProblem(502).ProducesProblem(504);
        return group;
    }

    /// <summary>校验起止日期格式、顺序及范围长度，然后调用考勤模块。</summary>
    /// <param name="startDate">必填，严格的 yyyy-MM-dd 起始日期。</param>
    /// <param name="endDate">必填，严格的 yyyy-MM-dd 结束日期，包含当天。</param>
    /// <param name="userId">可选完整员工 ID，与 user_name 互斥。</param>
    /// <param name="userName">可选员工姓名，去除首尾空白后精确匹配。</param>
    /// <param name="detail">simple 为默认摘要，full 附带完整上游明细。</param>
    /// <param name="service">考勤业务服务。</param>
    /// <param name="options">日期范围的服务端限制。</param>
    /// <param name="context">HTTP 请求上下文。</param>
    /// <param name="cancellationToken">客户端取消标记。</param>
    private static async Task<Results<Ok<AttendanceResponse[]>, ValidationProblem>> QueryAsync(
        [FromQuery(Name = "start_date")] string? startDate,
        [FromQuery(Name = "end_date")] string? endDate,
        [FromQuery(Name = "user_id")] string? userId,
        [FromQuery(Name = "user_name")] string? userName,
        [FromQuery(Name = "detail")] string? detail, AttendanceService service,
        IOptions<AttendanceOptions> options, HttpContext context, CancellationToken cancellationToken)
    {
        // 显式保留五个绑定参数供 OpenAPI 描述，实际验证统一读取请求，避免两个工具规则漂移。
        var query = AttendanceQueryParameters.Parse(context, options.Value, out var errors);
        if (query is null) return TypedResults.ValidationProblem(errors);
        return TypedResults.Ok(await service.QueryAsync(query.StartDate, query.EndDate,
            query.UserId, query.UserName, query.FullDetail, cancellationToken));
    }

    /// <summary>使用同一参数校验与考勤查询能力，只返回按配置筛选的加班日及汇总。</summary>
    /// <param name="startDate">必填起始工作日，包含当天。</param>
    /// <param name="endDate">必填结束工作日，包含当天。</param>
    /// <param name="userId">可选员工 ID，与姓名互斥。</param>
    /// <param name="userName">可选精确姓名，重名仍由原员工查询处理。</param>
    /// <param name="detail">simple 或 full，决定加班日的打卡明细层次。</param>
    /// <param name="service">复用考勤结果的加班筛选服务。</param>
    /// <param name="options">原考勤查询限制。</param>
    /// <param name="context">原 HTTP 查询参数。</param>
    /// <param name="cancellationToken">客户端取消标记。</param>
    private static async Task<Results<Ok<OvertimeResponse>, ValidationProblem>> QueryOvertimeAsync(
        [FromQuery(Name = "start_date")] string? startDate,
        [FromQuery(Name = "end_date")] string? endDate,
        [FromQuery(Name = "user_id")] string? userId,
        [FromQuery(Name = "user_name")] string? userName,
        [FromQuery(Name = "detail")] string? detail, OvertimeService service,
        IOptions<AttendanceOptions> options, HttpContext context, CancellationToken cancellationToken)
    {
        var query = AttendanceQueryParameters.Parse(context, options.Value, out var errors);
        if (query is null) return TypedResults.ValidationProblem(errors);
        return TypedResults.Ok(await service.QueryAsync(query, cancellationToken));
    }
}

/// <summary>描述日期范围参数，确保网关正确识别首尾日期和范围限制。</summary>
public sealed class AttendanceDateOperationFilter(IOptions<AttendanceOptions> options, IOptions<OvertimeOptions> overtime) : IOperationFilter
{
    /// <summary>为考勤查询补充日期约束，不修改其他功能的 OpenAPI 契约。</summary>
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (operation.OperationId is not ("attendance_query" or "attendance_overtime")) return;
        operation.Description += $" 单次最多 {options.Value.MaxQueryDays} 天。";
        operation.Description += operation.OperationId == "attendance_query"
            ? "查询失败的日期以 success=false 和 error 表示，成功日期仍返回。"
            : $"当前配置：{overtime.Value.WorkStartTime:HH:mm:ss} 上班、{overtime.Value.WorkEndTime:HH:mm:ss} 下班、{overtime.Value.ThresholdTime:HH:mm:ss}（含）起计加班；达到门槛后从正常下班时间计时。";
        foreach (var name in new[] { "start_date", "end_date" })
        {
            var parameter = operation.Parameters.Single(x => x.Name == name);
            parameter.Required = true;
            parameter.Description = name == "start_date" ? "起始工作日，包含当天，格式为 yyyy-MM-dd。" : "结束工作日，包含当天，格式为 yyyy-MM-dd。";
            parameter.Schema.Type = "string";
            parameter.Schema.Format = "date";
            parameter.Schema.Nullable = false;
            parameter.Schema.Pattern = @"^\d{4}-\d{2}-\d{2}$";
        }
        var detail = operation.Parameters.Single(x => x.Name == "detail");
        detail.Schema.Enum = new List<IOpenApiAny> { new OpenApiString("simple"), new OpenApiString("full") };
        detail.Schema.Default = new OpenApiString("simple");
    }
}
