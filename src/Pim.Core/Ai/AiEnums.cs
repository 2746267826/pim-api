namespace Pim.Core.Ai;

public enum AiMessageRole
{
    System,
    User,
    Assistant
}

public enum AiRequestStatus
{
    Succeeded,
    Failed,
    Blocked,
    TimedOut,
    FailedValidation
}
