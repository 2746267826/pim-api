namespace Pim.Module.Files.Entities;

public sealed class FileSuggestionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FileItemId { get; set; }
    public FileItemEntity? FileItem { get; set; }
    public string SuggestionType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public decimal Confidence { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = "pending";
    public Guid? AiRequestLogId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
