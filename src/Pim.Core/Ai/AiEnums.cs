using System.Text.Json.Serialization;

namespace Pim.Core.Ai;

public enum AiMessageRole
{
    System,
    User,
    Assistant
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiRequestStatus
{
    Succeeded,
    Failed,
    Blocked,
    TimedOut,
    FailedValidation
}
