using OfficeMcp.Api.Infrastructure.DingTalk;
using OfficeMcp.Api.Infrastructure.Errors;

namespace OfficeMcp.Api.Features.Employees;

/// <summary>遍历可见部门和分页员工，只有完整读取成功才发布五分钟内存缓存。</summary>
public sealed class EmployeeService(DingTalkClient dingTalk, TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CacheEntry? _cache;

    /// <summary>读取完整员工目录；并发请求共享刷新，超时或分页异常不返回半份目录。</summary>
    /// <param name="cancellationToken">调用方取消或整次考勤查询的截止标记。</param>
    public async Task<IReadOnlyList<EmployeeResponse>> GetAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            var cached = Volatile.Read(ref _cache);
            if (cached is not null && cached.ExpiresAt > clock.GetUtcNow()) return cached.Employees;
            await _refreshLock.WaitAsync(deadline.Token);
            try
            {
                cached = _cache;
                if (cached is not null && cached.ExpiresAt > clock.GetUtcNow()) return cached.Employees;
                var employees = await ReadDirectoryAsync(deadline.Token);
                Volatile.Write(ref _cache, new CacheEntry(employees, clock.GetUtcNow().AddMinutes(5)));
                return employees;
            }
            finally { _refreshLock.Release(); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpstreamException("employee_directory_timeout", "读取员工目录超时，请稍后重试。", statusCode: 504);
        }
    }

    /// <summary>按去除首尾空白后的姓名精确匹配；重名返回候选，禁止替调用者猜选。</summary>
    /// <param name="name">已经过接口校验的姓名。</param>
    /// <param name="cancellationToken">调用方取消标记。</param>
    public async Task<EmployeeResponse> ResolveNameAsync(string name, CancellationToken cancellationToken)
    {
        var matches = (await GetAsync(cancellationToken))
            .Where(employee => string.Equals(employee.Name.Trim(), name.Trim(), StringComparison.Ordinal)).ToArray();
        return matches.Length switch
        {
            0 => throw new ApiRequestException(404, "employee_not_found", "可见员工目录中没有匹配的姓名，请使用 employee_list 查询。"),
            1 => matches[0],
            _ => throw new ApiRequestException(409, "employee_name_ambiguous", "存在同名员工，请从候选中选择 user_id 重试。",
                new { candidates = matches })
        };
    }

    /// <summary>从根部门逐层读取子部门和员工，处理跨部门重复员工及游标循环。</summary>
    /// <param name="cancellationToken">目录读取截止或调用方取消标记。</param>
    /// <returns>按姓名和 ID 排序的完整可见员工目录。</returns>
    private async Task<EmployeeResponse[]> ReadDirectoryAsync(CancellationToken cancellationToken)
    {
        var pending = new Queue<EmployeeDepartment>();
        pending.Enqueue(new EmployeeDepartment(1, "根部门"));
        var visited = new HashSet<long> { 1 };
        var employees = new Dictionary<string, EmployeeResponse>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var department))
        {
            var cursors = new HashSet<long>();
            long cursor = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!cursors.Add(cursor)) throw IncompleteDirectory();
                var page = await dingTalk.PostAsync<DingTalkEmployeePage>("topapi/v2/user/list", new
                {
                    dept_id = department.DeptId, cursor, size = 100, contain_access_limit = false,
                    language = "zh_CN"
                }, cancellationToken);
                if (page.HasMore is null || page.List is null) throw IncompleteDirectory();
                foreach (var employee in page.List)
                {
                    if (employee is null || string.IsNullOrWhiteSpace(employee.UserId)
                        || string.IsNullOrWhiteSpace(employee.Name)) throw IncompleteDirectory();
                    if (employees.TryGetValue(employee.UserId, out var existing))
                    {
                        // 目录读取期间身份信息发生变化时不能据此判定姓名唯一。
                        if (existing.Name != employee.Name) throw IncompleteDirectory();
                        if (existing.Departments.All(x => x.DeptId != department.DeptId))
                            employees[employee.UserId] = existing with { Departments = [.. existing.Departments, department] };
                    }
                    else employees.Add(employee.UserId, new EmployeeResponse(employee.UserId, employee.Name, [department]));
                }
                if (!page.HasMore.Value) break;
                if (page.NextCursor is null or < 0 || page.List.Length == 0) throw IncompleteDirectory();
                cursor = page.NextCursor.Value;
            }

            var children = await dingTalk.PostAsync<DingTalkDepartment[]>("topapi/v2/department/listsub",
                new { dept_id = department.DeptId, language = "zh_CN" }, cancellationToken);
            foreach (var child in children)
            {
                if (child is null || child.DeptId is null or <= 0 || string.IsNullOrWhiteSpace(child.Name))
                    throw IncompleteDirectory();
                if (visited.Add(child.DeptId.Value)) pending.Enqueue(new EmployeeDepartment(child.DeptId.Value, child.Name));
            }
        }
        return employees.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.UserId, StringComparer.Ordinal).ToArray();
    }

    /// <summary>生成不含真实员工信息的目录完整性错误。</summary>
    private static UpstreamException IncompleteDirectory() =>
        new("employee_directory_incomplete", "员工目录响应不完整或分页异常，请稍后重试。");

    /// <summary>随宿主释放刷新锁。</summary>
    public void Dispose() => _refreshLock.Dispose();

    private sealed record CacheEntry(EmployeeResponse[] Employees, DateTimeOffset ExpiresAt);
}
