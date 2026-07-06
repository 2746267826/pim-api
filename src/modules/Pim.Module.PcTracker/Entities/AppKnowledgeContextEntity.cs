namespace Pim.Module.PcTracker.Entities;

public class AppKnowledgeContextEntity
{
    public Guid Id { get; set; }
    public Guid? AppSignatureId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string PatternType { get; set; } = string.Empty;
    public string PatternValue { get; set; } = string.Empty;
    public string? TargetCategoryName { get; set; }
    public string? ProjectTag { get; set; }
    public string ScopeSummary { get; set; } = string.Empty;
    public string Source { get; set; } = "user-confirmed";
    public double Confidence { get; set; } = 1.0;
    public bool Enabled { get; set; } = true;
    public int AffectedRecordCount { get; set; }
    public double AffectedDurationSeconds { get; set; }
    public DateTimeOffset? LastMatchedAt { get; set; }
    public Guid? SourceRuleId { get; set; }
    public Guid? SourceSuggestionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public AppSignatureEntity? AppSignature { get; set; }
}
