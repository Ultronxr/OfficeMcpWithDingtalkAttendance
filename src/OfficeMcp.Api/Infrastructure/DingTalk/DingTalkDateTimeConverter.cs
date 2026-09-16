using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OfficeMcp.Api.Infrastructure.DingTalk;

/// <summary>兼容钉钉本地时间字符串和毫秒时间戳，统一按北京时间解释无时区值。</summary>
public sealed class DingTalkDateTimeConverter : JsonConverter<DateTimeOffset?>
{
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);

    /// <summary>读取钉钉时间；空值和非正时间戳表示没有可用打卡时间。</summary>
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var text = reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number when reader.TryGetInt64(out var value) => value.ToString(CultureInfo.InvariantCulture),
            _ => throw new JsonException("无效的钉钉时间类型。")
        };
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
        {
            if (milliseconds <= 0) return null;
            try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToOffset(ChinaOffset); }
            catch (ArgumentOutOfRangeException) { throw new JsonException("钉钉时间戳超出有效范围。"); }
        }
        if (DateTime.TryParseExact(text, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFF"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), ChinaOffset);
        // ISO 时间必须自带时区，禁止借用服务运行机器的本地时区。
        if (text.Contains('T') && (text.EndsWith('Z') || text.LastIndexOf('+') > 10 || text.LastIndexOf('-') > 10)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
            return instant.ToOffset(ChinaOffset);
        throw new JsonException("无法解析钉钉时间。");
    }

    /// <summary>按 ISO 8601 输出带时区时间。</summary>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteStringValue(value.Value);
        else writer.WriteNullValue();
    }
}
