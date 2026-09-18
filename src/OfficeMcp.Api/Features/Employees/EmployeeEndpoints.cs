using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace OfficeMcp.Api.Features.Employees;

/// <summary>只读员工目录接口，复用办公服务认证及钉钉基础设施。</summary>
public static class EmployeeEndpoints
{
    /// <summary>注册单例目录缓存，供员工列表和按姓名查询共享。</summary>
    public static IServiceCollection AddEmployeeFeature(this IServiceCollection services) =>
        services.AddSingleton<EmployeeService>();

    /// <summary>注册 MCP 网关可导入的 employee_list 操作。</summary>
    public static RouteGroupBuilder MapEmployeeEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/employees", ListAsync).WithName("employee_list").WithTags("Employees")
            .WithSummary("获取应用可见员工列表")
            .WithDescription("遍历全部可见部门及子部门，自动处理员工分页并按 user_id 去重。返回完整员工 ID、姓名和可见所属部门；可用 user_name 按姓名包含匹配筛选。目录缓存五分钟。返回 ID 可用于 attendance_query。")
            .ProducesProblem(401).ProducesProblem(502).ProducesProblem(504);
        return group;
    }

    /// <summary>可选按姓名包含匹配筛选完整目录，筛选不会影响缓存内容。</summary>
    /// <param name="userName">可选姓名筛选文本；与考勤的姓名精确匹配规则不同。</param>
    /// <param name="service">完整目录服务。</param>
    /// <param name="context">用于检查重复参数的请求上下文。</param>
    /// <param name="cancellationToken">客户端取消标记。</param>
    private static async Task<Results<Ok<EmployeeResponse[]>, ValidationProblem>> ListAsync(
        [FromQuery(Name = "user_name")] string? userName, EmployeeService service, HttpContext context,
        CancellationToken cancellationToken)
    {
        if (context.Request.Query["user_name"].Count > 1 || userName?.Trim().Length > 100)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                { ["user_name"] = ["姓名筛选只能填写一个值，且不能超过 100 个字符。"] });
        var filter = userName?.Trim();
        var employees = await service.GetAsync(cancellationToken);
        return TypedResults.Ok(employees.Where(x => string.IsNullOrEmpty(filter)
            || x.Name.Contains(filter, StringComparison.Ordinal)).ToArray());
    }
}
