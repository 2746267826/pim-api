using Pim.Core.Operations;

namespace Pim.Module.Calendar.Services;

public static class ScheduleFactConfirmationPolicy
{
    private static readonly HashSet<string> DestructiveFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "stop-sync",
        "batch-delete",
        "bulk-writeback",
        "recurrence-wide-delete",
        "book-with-children"
    };

    private static readonly HashSet<string> CoreFactFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "title",
        "name",
        "dtStart",
        "dtEnd",
        "startsAt",
        "endsAt",
        "due",
        "plannedEnd",
        "location",
        "status",
        "project",
        "book",
        "owner",
        "recurrence",
        "rrule",
        "delete",
        "restore",
        "task-segment",
        "habit-rule"
    };

    public static ScheduleFactConfirmationDecision Classify(
        string source,
        IReadOnlyList<string> changedFields,
        bool externalWriteback = false)
    {
        if (changedFields.Any(field => DestructiveFields.Contains(field)))
        {
            return new ScheduleFactConfirmationDecision(
                OperationRiskLevel.L4BatchOrDestructiveGovernance,
                RequiresSecondLevelConfirmation: false,
                RequiresStrictConfirmation: true);
        }

        if (externalWriteback || string.Equals(source, "outlook", StringComparison.OrdinalIgnoreCase))
        {
            return new ScheduleFactConfirmationDecision(
                OperationRiskLevel.L3ExternalSourceOrWriteback,
                RequiresSecondLevelConfirmation: true,
                RequiresStrictConfirmation: false);
        }

        if (changedFields.Any(field => CoreFactFields.Contains(field)))
        {
            return new ScheduleFactConfirmationDecision(
                OperationRiskLevel.L2PimFactChange,
                RequiresSecondLevelConfirmation: false,
                RequiresStrictConfirmation: false);
        }

        return new ScheduleFactConfirmationDecision(
            OperationRiskLevel.L1LowRiskAction,
            RequiresSecondLevelConfirmation: false,
            RequiresStrictConfirmation: false);
    }
}

public sealed record ScheduleFactConfirmationDecision(
    OperationRiskLevel RiskLevel,
    bool RequiresSecondLevelConfirmation,
    bool RequiresStrictConfirmation);
