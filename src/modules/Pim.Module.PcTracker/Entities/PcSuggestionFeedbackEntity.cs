namespace Pim.Module.PcTracker.Entities;

public class PcSuggestionFeedbackEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? ProcessName { get; set; }
    public string? Domain { get; set; }
    public Guid? SuggestedCategoryId { get; set; }
    public Guid? AcceptedCategoryId { get; set; }
    public string Action { get; set; } = "rejected"; // accepted, rejected, modified
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
