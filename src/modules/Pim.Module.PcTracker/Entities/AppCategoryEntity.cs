namespace Pim.Module.PcTracker.Entities;

public class AppCategoryEntity
{
    public Guid Id { get; set; }
    public string AppPattern { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string Color { get; set; } = "#6B5EE4";
    public int Priority { get; set; }
    public bool IsBuiltin { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
