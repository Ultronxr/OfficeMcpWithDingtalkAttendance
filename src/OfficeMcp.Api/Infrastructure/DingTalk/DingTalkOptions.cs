namespace OfficeMcp.Api.Infrastructure.DingTalk;

/// <summary>钉钉企业内部应用配置，可通过所有钉钉业务功能共享。</summary>
public sealed class DingTalkOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 15;
}
