using System.Text.Json.Serialization;

namespace Pim.Core.Ai;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiRequestStatus
{
    Succeeded,
    Failed,
    Blocked,
    TimedOut,
    FailedValidation,
    Running,
    Cancelled,
    Incomplete
}
