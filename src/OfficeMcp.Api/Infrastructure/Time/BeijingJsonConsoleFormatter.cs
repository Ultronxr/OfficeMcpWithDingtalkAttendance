using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace OfficeMcp.Api.Infrastructure.Time;

/// <summary>输出单行 JSON 日志，时间固定北京时间，不依赖宿主时区或控制台前缀。</summary>
public sealed class BeijingJsonConsoleFormatter(TimeProvider clock) : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "office-beijing-json";
    private static readonly JsonSerializerOptions Json = OfficeTime.CreateJsonOptions();

    /// <summary>保留级别、分类、事件、消息和结构化状态，增加统一格式的时间戳。</summary>
    public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = entry.Formatter(entry.State, entry.Exception);
        if (message is null && entry.Exception is null) return;
        var state = entry.State is IEnumerable<KeyValuePair<string, object?>> properties
            ? properties.ToDictionary(x => x.Key, x => x.Value) : null;
        // 只序列化调用方已提供的安全字段，不额外输出请求、响应或配置。
        textWriter.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Timestamp"] = OfficeTime.Text(clock.GetUtcNow()), ["EventId"] = entry.EventId.Id,
            ["LogLevel"] = entry.LogLevel.ToString(), ["Category"] = entry.Category,
            ["Message"] = message, ["Exception"] = entry.Exception?.ToString(), ["State"] = state
        }, Json));
    }
}
