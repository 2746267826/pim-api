namespace Pim.Infrastructure.Ai;

public sealed class AiOptions
{
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "litellm";
    public string BaseUrl { get; set; } = "http://litellm:4000";
    public string ApiKey { get; set; } = string.Empty;
    public string DefaultModel { get; set; } = "pim-default";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxOutputTokensPerRequest { get; set; } = 1000;
    public int MaxAttemptsPerRequest { get; set; } = 2;
    public bool SaveFullPrompts { get; set; } = true;
    public bool SaveFullResponses { get; set; } = true;
}
