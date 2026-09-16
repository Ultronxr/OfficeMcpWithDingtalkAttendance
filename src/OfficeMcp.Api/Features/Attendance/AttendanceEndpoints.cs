using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OfficeMcp.Api.Features.Attendance;

/// <summary>考勤功能的依赖注册和 HTTP 接口，后续功能可在 Features 下并列添加。</summary>
public static class AttendanceEndpoints
{
    /// <summary>注册固定用户配置和考勤业务服务。</summary>
    public static IServiceCollection AddAttendanceFeature(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AttendanceOptions>().Bind(configuration.GetSection("Attendance"))
            .Validate(x => !string.IsNullOrWhiteSpace(x.UserId), "Attendance:UserId 不能为空。")
            .Validate(x => x.MaxQueryDays is >= 1 and <= 366, "Attendance:MaxQueryDays 必须在 1 至 366 之间。")
            .Validate(x => x.MaxParallelDays is >= 1 and <= 8, "Attendance:MaxParallelDays 必须在 1 至 8 之间。")
            .Validate(x => x.QueryTimeoutSeconds is >= 1 and <= 210, "Attendance:QueryTimeoutSeconds 必须在 1 至 210 秒之间。")
            .ValidateOnStart();
        services.AddScoped<AttendanceService>();
        services.AddSwaggerGen(options => options.OperationFilter<AttendanceDateOperationFilter>());
        return services;
    }

    /// <summary>注册只读的日期范围查询接口，并固定网关使用的 operationId。</summary>
    public static RouteGroupBuilder MapAttendanceEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/attendance", QueryAsync)
            .WithName("attendance_query").WithTags("Attendance")
            .WithSummary("查询固定用户日期范围内的考勤")
            .WithDescription("start_date 和 end_date 必填，格式为 yyyy-MM-dd，包含首尾日期；相同日期表示单日查询。返回按日期升序排列的数组，每日包含 success，失败时提供 error，其他日期继续返回。时间为北京时间。")
            .ProducesProblem(401).ProducesProblem(500);
        return group;
    }

    /// <summary>校验起止日期格式、顺序及范围长度，然后调用考勤模块。</summary>
    /// <param name="startDate">必填，严格的 yyyy-MM-dd 起始日期。</param>
    /// <param name="endDate">必填，严格的 yyyy-MM-dd 结束日期，包含当天。</param>
    /// <param name="service">考勤业务服务。</param>
    /// <param name="options">日期范围的服务端限制。</param>
    /// <param name="context">HTTP 请求上下文。</param>
    /// <param name="cancellationToken">客户端取消标记。</param>
    private static async Task<Results<Ok<AttendanceResponse[]>, ValidationProblem>> QueryAsync(
        [FromQuery(Name = "start_date")] string? startDate,
        [FromQuery(Name = "end_date")] string? endDate, AttendanceService service,
        IOptions<AttendanceOptions> options, HttpContext context, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var start = ParseDate("start_date", startDate, context, errors);
        var end = ParseDate("end_date", endDate, context, errors);
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);
        if (start > end)
            errors["end_date"] = ["结束日期不能早于开始日期。"];
        else if (end.DayNumber - start.DayNumber + 1 > options.Value.MaxQueryDays)
            errors["end_date"] = [$"单次查询最多 {options.Value.MaxQueryDays} 天，包含开始和结束日期。"];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);
        return TypedResults.Ok(await service.QueryAsync(start, end, cancellationToken));
    }

    /// <summary>读取严格的单值日期，将参数问题收集为统一的 400 响应。</summary>
    /// <param name="name">查询参数名称。</param>
    /// <param name="value">参数文本。</param>
    /// <param name="context">用于检查重复参数的 HTTP 上下文。</param>
    /// <param name="errors">待返回的参数错误集合。</param>
    private static DateOnly ParseDate(string name, string? value, HttpContext context,
        Dictionary<string, string[]> errors)
    {
        if (context.Request.Query[name].Count == 1 && value?.Length == 10
            && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        errors[name] = ["请提供唯一有效的日期，格式为 yyyy-MM-dd，例如 2026-09-10。"];
        return default;
    }
}

/// <summary>描述日期范围参数，确保网关正确识别首尾日期和范围限制。</summary>
public sealed class AttendanceDateOperationFilter(IOptions<AttendanceOptions> options) : IOperationFilter
{
    /// <summary>为考勤查询补充日期约束，不修改其他功能的 OpenAPI 契约。</summary>
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (operation.OperationId != "attendance_query") return;
        operation.Description += $" 单次最多 {options.Value.MaxQueryDays} 天；查询失败的日期以 success=false 和 error 表示，成功日期仍返回。";
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
    }
}
