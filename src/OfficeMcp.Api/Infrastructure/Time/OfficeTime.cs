using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OfficeMcp.Api.Infrastructure.Time;

/// <summary>全项目北京时间入口：保留实际瞬间，集中处理解析、输出、工作日和上游明细。</summary>
public static class OfficeTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(8);
    public const string Format = "yyyy-MM-dd'T'HH:mm:sszzz";
    public const string ClockFormat = "HH:mm:ss'+08:00'";
    public const string StorageFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz";
    public const string StorageClockFormat = "HH:mm:ss.fffffff'+08:00'";
    public const string Contract = "完整时间统一为 yyyy-MM-ddTHH:mm:ss+08:00（北京时间，显示到秒）；工作日期为 yyyy-MM-dd，时长保持数值，不返回 time_zone。";
    private static readonly string[] LocalFormats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd"];
    // 仅转换契约明确的时间字段，不能把记录 ID、状态码或时长误认为时间戳。
    private static readonly HashSet<string> DetailTimes = new(StringComparer.OrdinalIgnoreCase)
        { "workDate", "planCheckTime", "baseCheckTime", "userCheckTime", "gmtCreate", "gmtModified" };

    /// <summary>将同一瞬间规范为 UTC+8；不修改其 Unix 时间戳。</summary>
    /// <param name="value">带明确偏移的原始时间点。</param>
    /// <returns>同一瞬间的北京时间表示。</returns>
    public static DateTimeOffset InBeijing(DateTimeOffset value) => value.ToOffset(Offset);

    /// <summary>对外格式化到秒；仅内部持久化保留精度，展示省略小数但不四舍五入或修改输入。</summary>
    /// <param name="value">内部时钟、状态或上游解析得到的时间点。</param>
    /// <param name="preservePrecision">仅状态存储使用 true，避免重启改变期限或核验基线。</param>
    /// <returns>固定格式且带 +08:00 的可读字符串。</returns>
    public static string Text(DateTimeOffset value, bool preservePrecision = false) =>
        InBeijing(value).ToString(preservePrecision ? StorageFormat : Format, CultureInfo.InvariantCulture);

    /// <summary>按北京时间获取工作日期，与机器时区无关。</summary>
    /// <param name="value">需要判断北京时间日期的时间点。</param>
    public static DateOnly WorkDate(DateTimeOffset value) => DateOnly.FromDateTime(InBeijing(value).DateTime);

    /// <summary>设备协议只转换表示，绝不对 Unix 毫秒数值加八小时。</summary>
    /// <param name="value">自 Unix 纪元起的绝对毫秒。</param>
    public static DateTimeOffset FromUnixMilliseconds(long value) => InBeijing(DateTimeOffset.FromUnixTimeMilliseconds(value));

    /// <summary>解析有偏移或明确的无偏移日期时间；无偏移输入按北京时间处理。</summary>
    /// <param name="text">ISO 字符串或明确的北京时间日期／日期时间。</param>
    /// <returns>规范为 +08:00 的时间点；无效输入抛出 JsonException。</returns>
    public static DateTimeOffset Parse(string text)
    {
        try
        {
            if (Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$",
                    RegexOptions.CultureInvariant) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var instant)) return InBeijing(instant);
            if (DateTime.TryParseExact(text, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Offset);
        }
        catch (ArgumentException) { throw new JsonException("时间超出北京时间支持范围。"); }
        throw new JsonException("时间必须是有效的 ISO 日期时间或北京时间字符串。");
    }

    /// <summary>解析钉钉时间：兼容数字／字符串毫秒及无时区日期，空值和非正时间戳表示缺卡。</summary>
    /// <param name="element">上游契约已确认属于时间的字段，不能传入记录 ID 或时长。</param>
    public static DateTimeOffset? ReadDingTalk(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        var text = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => throw new JsonException("无效的钉钉时间类型。")
        };
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds)) return Parse(text);
        if (milliseconds <= 0) return null;
        try { return FromUnixMilliseconds(milliseconds); }
        catch (ArgumentException) { throw new JsonException("钉钉时间戳超出有效范围。"); }
    }

    /// <summary>所有业务 HTTP 与状态 JSON 共用配置，禁止各模块自行决定日期输出。</summary>
    /// <param name="options">尚未被使用并冻结的 JSON 序列化选项。</param>
    /// <param name="preservePrecision">仅内部状态启用；所有 MCP、HTTP 和日志默认显示到秒。</param>
    public static void ConfigureJson(JsonSerializerOptions options, bool preservePrecision = false)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.Converters.Add(new BeijingDateTimeConverter(preservePrecision));
        options.Converters.Add(new BeijingClockConverter(preservePrecision));
    }

    /// <summary>统一北京时间与字段命名；内部状态可保留精度，公开响应默认到秒。</summary>
    /// <param name="preservePrecision">仅用于内部状态文件，不用于工具响应或日志。</param>
    public static JsonSerializerOptions CreateJsonOptions(bool preservePrecision = false)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ConfigureJson(options, preservePrecision);
        return options;
    }

    /// <summary>递归规范明细中的已知时间；保留非时间字段与结构，不添加缺失字段。</summary>
    /// <param name="value">完整的钉钉打卡明细。</param>
    /// <returns>独立持有的可读明细；不修改传入 JSON 文档。</returns>
    public static JsonElement NormalizeDetails(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteDetails(writer, value);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>逐属性写入明细；识别驼峰／下划线时间字段，移除重复的时区元数据。</summary>
    private static void WriteDetails(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
            {
                var name = property.Name.Replace("_", "", StringComparison.Ordinal);
                if (name.Equals("timezone", StringComparison.OrdinalIgnoreCase)) continue;
                writer.WritePropertyName(property.Name);
                if (DetailTimes.Contains(name))
                {
                    var time = ReadDingTalk(property.Value);
                    if (time.HasValue) writer.WriteStringValue(Text(time.Value)); else writer.WriteNullValue();
                }
                else WriteDetails(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteDetails(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
