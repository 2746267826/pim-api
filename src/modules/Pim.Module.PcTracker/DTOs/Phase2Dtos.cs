namespace Pim.Module.PcTracker.DTOs;

public class CategoryTreeNode
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#64748b";
    public string? Icon { get; set; }
    public string Productivity { get; set; } = "neutral";
    public int SortOrder { get; set; }
    public bool IsBuiltin { get; set; }
    public List<CategoryTreeNode> Children { get; set; } = new();
}

public class CategorySaveRequest
{
    public Guid? Id { get; set; }
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#64748b";
    public string? Icon { get; set; }
    public string Productivity { get; set; } = "neutral";
    public int SortOrder { get; set; }
}

public class ReorderCategoriesRequest
{
    public List<ReorderItem> Items { get; set; } = new();
}

public class ReorderItem
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public int SortOrder { get; set; }
}

public class DailyProductivityDto
{
    public string Date { get; set; } = string.Empty;
    public double ProductiveMinutes { get; set; }
    public double NeutralMinutes { get; set; }
    public double DistractingMinutes { get; set; }
    public double TotalMinutes { get; set; }
    public double ProductiveRatio { get; set; }
}

public class ProductivityGoalDto
{
    public double DailyProductiveHours { get; set; } = 5.0;
}

public class ProductivityDashboardDto
{
    public double TodayScore { get; set; }
    public double ProductiveHours { get; set; }
    public double DistractingHours { get; set; }
    public double NeutralHours { get; set; }
    public double TargetHours { get; set; }
    public bool GoalMet { get; set; }
    public List<DailyProductivityDto> WeeklyTrend { get; set; } = new();
}

public class TimelineV2Item
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string AppName { get; set; } = string.Empty;
    public string? WindowTitle { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string? CategoryColor { get; set; }
    public string Productivity { get; set; } = "neutral";
    public double Confidence { get; set; }
    public double DurationMinutes { get; set; }
}
