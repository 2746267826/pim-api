using System.Text.Json.Serialization;

namespace Pim.Core.Operations;

public enum PimHealthStatus
{
    Unknown = 0,
    Healthy = 1,
    Warning = 2,
    Critical = 3
}

public enum StatusComponentKind
{
    Api,
    Database,
    Storage,
    TextExtraction,
    Daemon,
    ActivityWatch,
    KeyStats,
    BackgroundJobs
}

public enum AuditActorType
{
    User,
    Daemon,
    System,
    Job,
    Mcp
}

public enum AuditResult
{
    Success,
    Failure,
    PendingConfirmation,
    Rejected
}

public enum OperationConfirmationStatus
{
    Pending,
    Confirmed,
    Rejected,
    Expired,
    Executed
}

public enum OperationRiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
    L0AutomaticArtifact = 10,
    L1LowRiskAction = 11,
    L2PimFactChange = 12,
    L3ExternalSourceOrWriteback = 13,
    L4BatchOrDestructiveGovernance = 14
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DaemonSourceState
{
    Unknown,
    Available,
    Unavailable,
    Paused
}
