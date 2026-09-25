using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OfficeMcp.Api.Infrastructure.Time;

/// <summary>全局声明时间输出格式，新增工具自动继承约定。</summary>
public sealed class TimeSchemaFilter : ISchemaFilter, IOperationFilter
{
    /// <summary>网关通常只导入操作说明；每个新工具自动继承时间约定，不依赖顶层说明被转发。</summary>
    public void Apply(OpenApiOperation operation, OperationFilterContext context) =>
        operation.Description = (operation.Description ?? "") + "\n" + OfficeTime.Contract;

    /// <summary>区分完整时间和每日时刻；示例固定为合成值，不含真实考勤。</summary>
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        var type = Nullable.GetUnderlyingType(context.Type) ?? context.Type;
        if (type != typeof(DateTimeOffset) && type != typeof(TimeOnly)) return;
        schema.Type = "string";
        schema.Format = type == typeof(DateTimeOffset) ? "date-time" : "time";
        schema.Description = OfficeTime.Contract;
        schema.Example = new OpenApiString(type == typeof(DateTimeOffset)
            ? "2026-09-25T09:00:00+08:00" : "09:00:00+08:00");
    }
}
