using System.Net;
using System.Text.Json;

namespace OfficeMcp.Api.Tests;

/// <summary>验证完整可见目录、分页去重、姓名选择以及不完整目录的失败行为。</summary>
public sealed class EmployeeQueryTests
{
    private const string AttendancePath = "/api/attendance?start_date=2026-09-10&end_date=2026-09-10";

    /// <summary>递归分页得到完整目录，跨部门去重后合并部门，并在过期前复用缓存。</summary>
    [Fact]
    public async Task TraversesPagesAndDescendantsAndCachesCompleteDirectory()
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.DirectoryResponse = DirectoryFixture;
        using var client = factory.AuthenticatedClient();
        // 并发冷启动也只遍历一次目录。
        var bodies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.GetStringAsync("/api/employees")));
        using var document = JsonDocument.Parse(bodies[0]);
        Assert.Equal(2, document.RootElement.GetArrayLength());
        var first = document.RootElement.EnumerateArray().Single(x => x.GetProperty("user_id").GetString() == "employee-one");
        Assert.Equal(3, first.GetProperty("departments").GetArrayLength());
        Assert.Equal(7, factory.Handler.DirectoryRequests.Count);
        using var filtered = JsonDocument.Parse(await client.GetStringAsync("/api/employees?user_name=李"));
        Assert.Single(filtered.RootElement.EnumerateArray());
        Assert.Equal(7, factory.Handler.DirectoryRequests.Count);
        factory.Clock.Advance(TimeSpan.FromMinutes(6));
        _ = await client.GetStringAsync("/api/employees");
        Assert.Equal(14, factory.Handler.DirectoryRequests.Count);
    }

    /// <summary>按姓名精确选中员工；按 ID 查询不依赖目录，两个参数都省略则使用默认员工。</summary>
    [Theory]
    [InlineData("&user_name=%20李四%20", "employee-two", true)]
    [InlineData("&user_id=other-employee", "other-employee", false)]
    [InlineData("", OfficeApiFactory.UserId, false)]
    public async Task SelectsRequestedEmployee(string query, string expectedId, bool usesDirectory)
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.DirectoryResponse = DirectoryFixture;
        using var client = factory.AuthenticatedClient();
        using var response = await client.GetAsync(AttendancePath + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var sent = JsonDocument.Parse(Assert.Single(factory.Handler.Bodies));
        Assert.Equal(expectedId, Assert.Single(sent.RootElement.GetProperty("userIds").EnumerateArray()).GetString());
        Assert.Equal(usesDirectory, !factory.Handler.DirectoryRequests.IsEmpty);
        if (usesDirectory)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("李四", body.RootElement[0].GetProperty("user_name").GetString());
        }
    }

    /// <summary>姓名不存在或同名时不发送考勤请求，同名响应提供可直接重试的候选 ID。</summary>
    [Theory]
    [InlineData("不存在", HttpStatusCode.NotFound, "employee_not_found")]
    [InlineData("张三", HttpStatusCode.Conflict, "employee_name_ambiguous")]
    public async Task DoesNotGuessWhenNameIsMissingOrAmbiguous(string name, HttpStatusCode status, string code)
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.DirectoryResponse = (path, body) => DirectoryFixture(path, body).Replace("李四", "张三");
        using var client = factory.AuthenticatedClient();
        var response = await client.GetAsync(AttendancePath + "&user_name=" + Uri.EscapeDataString(name));
        Assert.Equal(status, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        if (status == HttpStatusCode.Conflict)
        {
            var candidates = document.RootElement.GetProperty("details").GetProperty("candidates");
            Assert.Equal(2, candidates.GetArrayLength());
            Assert.Contains(candidates.EnumerateArray(), x => x.GetProperty("user_id").GetString() == "employee-two");
        }
        Assert.Equal(0, factory.Handler.AttendanceCalls);
    }

    /// <summary>分页或子部门读取失败不能据半份目录选人，也不能污染后续完整缓存。</summary>
    [Theory]
    [InlineData("missing_cursor")]
    [InlineData("cursor_loop")]
    [InlineData("missing_page_state")]
    [InlineData("department_failure")]
    public async Task IncompleteDirectoryIsNeverUsedOrCached(string fault)
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.DirectoryResponse = (path, body) =>
        {
            if (fault == "department_failure")
                return path.EndsWith("/listsub", StringComparison.Ordinal)
                    ? """{"errcode":60011}""" : DirectoryFixture(path, body);
            return fault switch
            {
                "missing_cursor" => """{"errcode":0,"result":{"has_more":true,"list":[{"userid":"one","name":"张三"}]}}""",
                "cursor_loop" => """{"errcode":0,"result":{"has_more":true,"next_cursor":0,"list":[{"userid":"one","name":"张三"}]}}""",
                _ => """{"errcode":0,"result":{"list":[{"userid":"one","name":"张三"}]}}"""
            };
        };
        using var client = factory.AuthenticatedClient();
        Assert.Equal(HttpStatusCode.BadGateway, (await client.GetAsync(AttendancePath + "&user_name=张三")).StatusCode);
        Assert.Equal(0, factory.Handler.AttendanceCalls);
        factory.Handler.DirectoryResponse = DirectoryFixture;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(AttendancePath + "&user_name=张三")).StatusCode);
        using var sent = JsonDocument.Parse(Assert.Single(factory.Handler.Bodies));
        Assert.Equal("employee-one", sent.RootElement.GetProperty("userIds")[0].GetString());
    }

    /// <summary>构造三层部门、两页根部门员工及循环部门边，所有身份均为合成数据。</summary>
    private static string DirectoryFixture(string path, string body)
    {
        using var document = JsonDocument.Parse(body);
        var department = document.RootElement.GetProperty("dept_id").GetInt64();
        if (path.EndsWith("/listsub", StringComparison.Ordinal)) return department switch
        {
            1 => """{"errcode":0,"result":[{"dept_id":2,"name":"研发"}]}""",
            2 => """{"errcode":0,"result":[{"dept_id":3,"name":"平台"}]}""",
            _ => """{"errcode":0,"result":[{"dept_id":1,"name":"根部门"}]}"""
        };
        Assert.Equal(100, document.RootElement.GetProperty("size").GetInt32());
        if (department == 1)
            return document.RootElement.GetProperty("cursor").GetInt64() == 0
                ? """{"errcode":0,"result":{"has_more":true,"next_cursor":100,"list":[{"userid":"employee-one","name":"张三"}]}}"""
                : """{"errcode":0,"result":{"has_more":false,"list":[{"userid":"employee-two","name":"李四"}]}}""";
        return """{"errcode":0,"result":{"has_more":false,"list":[{"userid":"employee-one","name":"张三"}]}}""";
    }
}
