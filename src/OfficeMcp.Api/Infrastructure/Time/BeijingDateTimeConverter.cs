using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OfficeMcp.Api.Infrastructure.Time;

/// <summary>全局日期时间转换器，兼容旧 UTC 状态，写出固定格式的北京时间。</summary>
public sealed class BeijingDateTimeConverter(bool preservePrecision = false) : JsonConverter<DateTimeOffset>
{
    /// <summary>只读取日期时间字符串，不把业务数字或宿主时区隐式当作时间。</summary>
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? OfficeTime.Parse(reader.GetString()!) : throw new JsonException("需要日期时间字符串。");

    /// <summary>不论内部偏移，输出同一瞬间的 UTC+8 表示。</summary>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(OfficeTime.Text(value, preservePrecision));
}

/// <summary>每日重复的配置时刻没有日期，显式附上 +08:00，不编造工作日。</summary>
public sealed class BeijingClockConverter(bool preservePrecision = false) : JsonConverter<TimeOnly>
{
    /// <summary>兼容既有无偏移时刻和统一北京时间时刻，不接受含糊的其他时区时刻。</summary>
    public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("需要北京时间时刻字符串。");
        var text = reader.GetString()!;
        if (text.EndsWith("+08:00", StringComparison.Ordinal)) text = text[..^6];
        return TimeOnly.TryParseExact(text, ["HH:mm", "HH:mm:ss", "HH:mm:ss.FFFFFFF"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : throw new JsonException("无效的北京时间时刻。");
    }

    /// <summary>统一时刻精度并附上偏移。</summary>
    public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(preservePrecision ? OfficeTime.StorageClockFormat : OfficeTime.ClockFormat, CultureInfo.InvariantCulture));
}
