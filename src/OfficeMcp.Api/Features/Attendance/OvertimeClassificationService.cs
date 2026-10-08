using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OfficeMcp.Api.Infrastructure.DingTalk;
using OfficeMcp.Api.Infrastructure.Errors;
using OfficeMcp.Api.Infrastructure.Time;

namespace OfficeMcp.Api.Features.Attendance;

/// <summary>读取钉钉原生休息安排，动态发现企业报表列，不使用加班时长列或星期推断。</summary>
public sealed class OvertimeClassificationService(DingTalkClient dingTalk)
{
    /// <summary>仅查询有考勤记录的日期；31 天分段，分类失败仍为每个目标日期保留错误事实。</summary>
    /// <param name="dates">成功取得考勤记录、需要判断日期类型的工作日。</param>
    /// <param name="userId">由考勤模块选择的完整员工 ID，不使用公开脱敏 ID。</param>
    /// <param name="cancellationToken">客户端取消标记，取消时立即终止而不是返回伪造部分结果。</param>
    /// <param name="queryToken">整次查询剩余时间预算；耗尽时保留已完成分类，其余标为未知。</param>
    /// <returns>每个目标日期都有 confirmed 或 unknown 分类，错误中不包含上游原始正文。</returns>
    public async Task<IReadOnlyDictionary<DateOnly, OvertimeDayClassification>> QueryAsync(
        IReadOnlyList<DateOnly> dates, string userId, CancellationToken cancellationToken, CancellationToken queryToken)
    {
        var ordered = dates.Distinct().Order().ToArray();
        var results = new Dictionary<DateOnly, OvertimeDayClassification>();
        if (ordered.Length == 0) return results;
        long columnId;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var columns = await dingTalk.PostAsync<ReportColumns>("topapi/attendance/getattcolumns", new { }, queryToken);
            // alias 不受企业列重命名影响；ID 必须来自当前企业，不能硬编码采样值。
            var matching = columns.Columns?.Where(column => column?.Alias == "attendance_rest_days").ToArray();
            if (matching is not { Length: 1 } || matching[0].Id is not > 0) throw InvalidResponse();
            columnId = matching[0].Id!.Value;
        }
        catch (UpstreamException exception)
        {
            return ordered.ToDictionary(date => date, _ => Unknown(SafeFailure(exception)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ordered.ToDictionary(date => date, _ => Unknown(TimeoutError()));
        }

        for (var offset = 0; offset < ordered.Length;)
        {
            var start = ordered[offset];
            var end = DateOnly.FromDayNumber(Math.Min(start.DayNumber + 30, ordered[^1].DayNumber));
            var wanted = ordered.Skip(offset).TakeWhile(date => date <= end).ToArray();
            offset += wanted.Length;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                queryToken.ThrowIfCancellationRequested();
                var report = await dingTalk.PostAsync<ReportValues>("topapi/attendance/getcolumnval", new
                {
                    userid = userId,
                    column_id_list = columnId.ToString(CultureInfo.InvariantCulture),
                    from_date = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 00:00:00",
                    to_date = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 23:59:59"
                }, queryToken);
                var segment = ReadSegment(report, columnId, start, end, wanted);
                foreach (var (date, value) in segment) results[date] = value;
            }
            catch (UpstreamException exception)
            {
                foreach (var date in wanted) results[date] = Unknown(SafeFailure(exception));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 截止时只补全尚未处理的日期，不能丢弃前面已经读取的有效分类。
                foreach (var date in ordered.Where(date => !results.ContainsKey(date)))
                    results[date] = Unknown(TimeoutError());
                break;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    /// <summary>校验列身份、日期范围和唯一性；缺失或不明确的单日值不能被默认为工作日。</summary>
    private static Dictionary<DateOnly, OvertimeDayClassification> ReadSegment(ReportValues report, long columnId,
        DateOnly start, DateOnly end, IReadOnlyList<DateOnly> wanted)
    {
        try
        {
            var columns = report.Columns?.Where(column => column?.Column?.Id == columnId).ToArray();
            if (columns is not { Length: 1 } || columns[0].Values is null) throw InvalidResponse();
            var values = new Dictionary<DateOnly, JsonElement>();
            foreach (var entry in columns[0].Values!)
            {
                if (entry is null) throw InvalidResponse();
                var instant = OfficeTime.ReadDingTalk(entry.Date) ?? throw new JsonException();
                var date = OfficeTime.WorkDate(instant);
                if (date < start || date > end || !values.TryAdd(date, entry.Value)) throw InvalidResponse();
            }
            return wanted.ToDictionary(date => date, date => values.TryGetValue(date, out var value)
                ? ReadClassification(value) : Unknown(new("overtime_classification_missing", "钉钉未返回此日期的休息安排，保留记录待核验。")));
        }
        catch (JsonException) { throw InvalidResponse(); }
    }

    /// <summary>休息天数只接受明确的 0 或 1；空值、分数和未知值不转换成确定分类。</summary>
    private static OvertimeDayClassification ReadClassification(JsonElement value)
    {
        var text = value.ValueKind is JsonValueKind.Number or JsonValueKind.String ? value.ToString() : null;
        if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign
                | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out var number)
            && number is 0 or 1) return new("confirmed", "dingtalk_report", number == 1);
        return Unknown(new("overtime_classification_ambiguous", "钉钉休息安排值不明确，保留记录待核验。"));
    }

    /// <summary>构造可公开的未知分类；调用方仍必须保留已有考勤记录。</summary>
    private static OvertimeDayClassification Unknown(AttendanceError error) => new("unknown", "dingtalk_report", null, error);

    /// <summary>保留稳定错误码和提供方错误码，禁止外泄原始正文及员工 ID。</summary>
    private static AttendanceError SafeFailure(UpstreamException exception) => new(
        exception.Code == "overtime_classification_invalid_response" ? exception.Code : "overtime_classification_failed",
        "读取钉钉日期分类失败，已有记录保留为待核验出勤。", exception.ProviderCode);

    /// <summary>共享剩余时间预算耗尽时的逐日分类错误。</summary>
    private static AttendanceError TimeoutError() => new("overtime_classification_timeout", "日期分类查询超时，已有记录保留为待核验出勤。");

    /// <summary>不含真实响应数据的上游格式异常。</summary>
    private static UpstreamException InvalidResponse() => new("overtime_classification_invalid_response", "钉钉休息安排响应不符合约定。");

    private sealed class ReportColumns
    {
        [JsonPropertyName("columns")] public List<ReportColumn>? Columns { get; init; }
    }

    private sealed class ReportColumn
    {
        [JsonPropertyName("id")] public long? Id { get; init; }
        [JsonPropertyName("alias")] public string? Alias { get; init; }
    }

    private sealed class ReportValues
    {
        [JsonPropertyName("column_vals")] public List<ReportColumnValues>? Columns { get; init; }
    }

    private sealed class ReportColumnValues
    {
        [JsonPropertyName("column_vo")] public ReportColumn? Column { get; init; }
        [JsonPropertyName("column_vals")] public List<ReportDateValue>? Values { get; init; }
    }

    private sealed class ReportDateValue
    {
        [JsonPropertyName("date")] public JsonElement Date { get; init; }
        [JsonPropertyName("value")] public JsonElement Value { get; init; }
    }
}
