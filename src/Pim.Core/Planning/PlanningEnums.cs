namespace Pim.Core.Planning;

public enum TaskPlanningState
{
    Inbox,
    ToPlan,
    Planned,
    InProgress,
    Waiting,
    Blocked,
    Deferred,
    Paused,
    Completed,
    Cancelled
}

public enum TaskSegmentStatus
{
    Planned,
    Active,
    Paused,
    Completed,
    Cancelled
}

public enum HabitCadence
{
    Daily,
    Weekly,
    Monthly,
    Custom
}

public enum ReminderChannel
{
    Web,
    WindowsToast,
    AndroidNotification,
    Email
}

public enum ReminderStatus
{
    Open,
    Snoozed,
    Sent,
    Acknowledged,
    Dismissed,
    Failed
}

public enum ReportKind
{
    Daily,
    Weekly,
    Monthly,
    Project
}

public enum PlanningSource
{
    Manual,
    Pim,
    Outlook,
    Ai,
    Template,
    Import
}
