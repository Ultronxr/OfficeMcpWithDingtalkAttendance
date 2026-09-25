using System.Text.Json;
using System.Text.Json.Serialization;
using OfficeMcp.Api.Infrastructure.Time;

namespace OfficeMcp.Api.Infrastructure.DingTalk;

/// <summary>钉钉协议适配器；所有解析和格式化都委托全项目时间工具。</summary>
public sealed class DingTalkDateTimeConverter : JsonConverter<DateTimeOffset?>
{
    /// <summary>兼容毫秒与本地时间；空值、非正时间戳按钉钉协议表示缺卡。</summary>
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return OfficeTime.ReadDingTalk(document.RootElement);
    }

    /// <summary>上游适配模型若被写出也必须使用统一格式。</summary>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteStringValue(OfficeTime.Text(value.Value)); else writer.WriteNullValue();
    }
}
