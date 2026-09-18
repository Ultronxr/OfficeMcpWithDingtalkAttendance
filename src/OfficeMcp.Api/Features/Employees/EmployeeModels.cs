using System.Text.Json.Serialization;

namespace OfficeMcp.Api.Features.Employees;

/// <summary>可用于考勤查询的员工，不包含电话、邮箱等无关个人资料。</summary>
/// <param name="UserId">完整钉钉员工 ID，可直接作为 attendance_query 的 user_id。</param>
/// <param name="Name">员工姓名。</param>
/// <param name="Departments">应用可见的所属部门，用于区分重名员工。</param>
public sealed record EmployeeResponse(string UserId, string Name, IReadOnlyList<EmployeeDepartment> Departments);

/// <summary>员工所属的可见部门。</summary>
public sealed record EmployeeDepartment(long DeptId, string Name);

/// <summary>钉钉部门员工分页结果；缺失分页状态视为不完整响应。</summary>
internal sealed class DingTalkEmployeePage
{
    [JsonPropertyName("has_more")] public bool? HasMore { get; init; }
    [JsonPropertyName("next_cursor")] public long? NextCursor { get; init; }
    [JsonPropertyName("list")] public DingTalkEmployee[]? List { get; init; }
}

/// <summary>仅读取姓名查询和去重需要的员工字段。</summary>
internal sealed class DingTalkEmployee
{
    [JsonPropertyName("userid")] public string? UserId { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
}

/// <summary>钉钉子部门信息，用于递归读取应用可见目录。</summary>
internal sealed class DingTalkDepartment
{
    [JsonPropertyName("dept_id")] public long? DeptId { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
}
