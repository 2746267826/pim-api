namespace Pim.Module.PcTracker.Entities;

public class AppSignatureEntity
{
    public Guid Id { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? CategoryPath { get; set; }
    public string? Productivity { get; set; }
    public string? Description { get; set; }
    public string Source { get; set; } = "builtin";
    public double Confidence { get; set; } = 1.0;
    public string? Icon { get; set; }
    public string? SearchKeywords { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
