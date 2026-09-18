using System.Globalization;
using System.Net;
using System.Text.Json;

namespace OfficeMcp.Api.Tests;

/// <summary>覆盖范围边界、按日部分失败、稳定排序和整次查询截止时间。</summary>
public sealed class AttendanceRangeTests
{
    /// <summary>多段乱序完成时仍按工作日合并真实记录，且同时在途的请求不超过配置值。</summary>
    [Fact]
    public async Task MergesSegmentRecordsWithBoundedConcurrency()
    {
        await using var factory = new OfficeApiFactory();
        factory.ConfigureAttendance = options => options.MaxParallelDays = 2;
        var active = 0;
        var peak = 0;
        var peakGate = new object();
        factory.Handler.AttendanceResponseAsync = async (body, token) =>
        {
            using var request = JsonDocument.Parse(body);
            var start = request.RootElement.GetProperty("checkDateFrom").GetString()!;
            var current = Interlocked.Increment(ref active);
            lock (peakGate) peak = Math.Max(peak, current);
            try
            {
                await Task.Delay(start.StartsWith("2026-09-01", StringComparison.Ordinal) ? 80 : 5, token);
                return $$"""{"errcode":0,"recordresult":[{"id":{{int.Parse(start.Substring(8, 2), CultureInfo.InvariantCulture)}},"userId":"test-user-000001","workDate":"{{start}}","userCheckTime":"{{start}}"}]}""";
            }
            finally { Interlocked.Decrement(ref active); }
        };
        using var client = factory.AuthenticatedClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/attendance?start_date=2026-09-01&end_date=2026-09-15"));
        Assert.Equal(15, document.RootElement.GetArrayLength());
        for (var index = 0; index < 15; index++)
        {
            var day = document.RootElement[index];
            Assert.True(day.GetProperty("success").GetBoolean());
            Assert.Equal(index % 7 == 0 ? 1 : 0, day.GetProperty("records").GetArrayLength());
        }
        Assert.Equal(2, peak);
        Assert.Equal(3, factory.Handler.AttendanceCalls);
    }

    /// <summary>首尾均包含在范围中，正确处理闰日、跨年、最大日期以及完整月份。</summary>
    [Theory]
    [InlineData("2024-02-28", "2024-03-01", 3)]
    [InlineData("2026-02-28", "2026-03-01", 2)]
    [InlineData("2026-12-31", "2027-01-01", 2)]
    [InlineData("9999-12-31", "9999-12-31", 1)]
    [InlineData("2026-08-01", "2026-08-31", 31)]
    public async Task IncludesEveryDateInAscendingOrder(string start, string end, int count)
    {
        await using var factory = new OfficeApiFactory();
        using var client = factory.AuthenticatedClient();
        var response = await client.GetAsync($"/api/attendance?start_date={start}&end_date={end}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var dates = document.RootElement.EnumerateArray().ToArray();
        var first = DateOnly.ParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal(count, dates.Length);
        for (var index = 0; index < count; index++)
        {
            Assert.Equal(first.AddDays(index).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), dates[index].GetProperty("work_date").GetString());
            Assert.True(dates[index].GetProperty("success").GetBoolean());
            Assert.Equal("tes**********001", dates[index].GetProperty("user_id").GetString());
        }
        Assert.Equal((count + 6) / 7, factory.Handler.AttendanceCalls);
        // 检查每段闭区间，覆盖月底、闰日与最大日期，防止遗漏或重复日期。
        var requests = factory.Handler.Bodies.Select(body => JsonDocument.Parse(body))
            .OrderBy(doc => doc.RootElement.GetProperty("checkDateFrom").GetString(), StringComparer.Ordinal).ToArray();
        try
        {
            for (var index = 0; index < requests.Length; index++)
            {
                var segmentStart = first.AddDays(index * 7);
                var segmentEnd = first.AddDays(Math.Min(index * 7 + 6, count - 1));
                Assert.Equal(segmentStart.ToString("yyyy-MM-dd") + " 00:00:00",
                    requests[index].RootElement.GetProperty("checkDateFrom").GetString());
                Assert.Equal(segmentEnd.ToString("yyyy-MM-dd") + " 23:59:59",
                    requests[index].RootElement.GetProperty("checkDateTo").GetString());
            }
        }
        finally { foreach (var request in requests) request.Dispose(); }
        Assert.Equal(1, factory.Handler.TokenCalls);
    }

    /// <summary>中间分段失败时返回其错误，成功日期及按日期排序均保留。</summary>
    [Fact]
    public async Task OneSegmentFailureDoesNotDiscardOtherDays()
    {
        await using var factory = new OfficeApiFactory();
        factory.Handler.AttendanceResponseAsync = async (body, token) =>
        {
            using var request = JsonDocument.Parse(body);
            var date = request.RootElement.GetProperty("checkDateFrom").GetString()!;
            // 刻意让较晚日期先完成，以检验输出排序而非请求完成顺序。
            await Task.Delay(date.StartsWith("2026-09-10", StringComparison.Ordinal) ? 80 : 5, token);
            return date.StartsWith("2026-09-17", StringComparison.Ordinal)
                ? "{\"errcode\":60011,\"errmsg\":\"PRIVATE-ERROR\"}"
                : "{\"errcode\":0,\"recordresult\":[]}";
        };
        using var client = factory.AuthenticatedClient();
        var response = await client.GetAsync("/api/attendance?start_date=2026-09-10&end_date=2026-09-24");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var days = document.RootElement;
        Assert.Equal(15, days.GetArrayLength());
        Assert.True(days[0].GetProperty("success").GetBoolean());
        Assert.All(days.EnumerateArray().Skip(7).Take(7), day => Assert.False(day.GetProperty("success").GetBoolean()));
        Assert.True(days[14].GetProperty("success").GetBoolean());
        Assert.Equal("2026-09-17", days[7].GetProperty("work_date").GetString());
        Assert.Equal("60011", days[7].GetProperty("error").GetProperty("provider_code").GetString());
        Assert.DoesNotContain("PRIVATE-ERROR", body);
        Assert.DoesNotContain(OfficeApiFactory.UserId, body);
    }

    /// <summary>整次超时保留已成功日期，其余日期显式返回超时，不能伪装成无记录。</summary>
    [Fact]
    public async Task DeadlinePreservesCompletedDatesAndMarksUnfinishedDates()
    {
        await using var factory = new OfficeApiFactory();
        factory.ConfigureAttendance = options =>
        {
            options.QueryTimeoutSeconds = 1;
            options.MaxParallelDays = 1;
        };
        factory.Handler.AttendanceResponseAsync = async (body, token) =>
        {
            using var request = JsonDocument.Parse(body);
            if (!request.RootElement.GetProperty("checkDateFrom").GetString()!.StartsWith("2026-09-10", StringComparison.Ordinal))
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "{\"errcode\":0,\"recordresult\":[]}";
        };
        using var client = factory.AuthenticatedClient();
        var response = await client.GetAsync("/api/attendance?start_date=2026-09-10&end_date=2026-09-24");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement[0].GetProperty("success").GetBoolean());
        foreach (var day in document.RootElement.EnumerateArray().Skip(7))
        {
            Assert.False(day.GetProperty("success").GetBoolean());
            Assert.Equal("attendance_query_timeout", day.GetProperty("error").GetProperty("code").GetString());
        }
        Assert.Equal(2, factory.Handler.AttendanceCalls);
    }
}
