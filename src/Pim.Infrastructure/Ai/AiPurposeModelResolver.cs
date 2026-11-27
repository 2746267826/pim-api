using Pim.Core.Ai;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Ai;

/// <summary>
/// Resolves the model configured for a purpose. Only an unset purpose falls back to the legacy
/// type defaults; an explicitly configured purpose never silently switches to another model.
/// </summary>
public static class AiPurposeModelResolver
{
    /// <summary>
    /// Resolves the model for a purpose. For note organization the legacy type default depends on
    /// the material: an image-bearing note used the vision default, while a text note used the
    /// text default. An explicitly configured purpose always wins over either legacy fallback.
    /// </summary>
    public static Guid? ResolveId(AiModelRuntimeEntity? runtime, AiPurpose purpose, bool hasImages = false) =>
        purpose switch
    {
        AiPurpose.NoteOrganization => runtime?.NoteOrganizationModelId ??
            (hasImages ? runtime?.VisionModelId ?? runtime?.TextModelId : runtime?.TextModelId),
        AiPurpose.NoteTodo => runtime?.NoteTodoModelId ?? runtime?.TextModelId,
        AiPurpose.CalendarPlanning => runtime?.CalendarPlanningModelId ?? runtime?.TextModelId,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown AI purpose")
    };

    /// <summary>
    /// Default shown by the destination picker when no source material is known yet. Unlike
    /// <see cref="ResolveId"/>, this may fall back from text to vision so a vision-only setup can
    /// still be preselected for an image note; the backend re-validates the selected model against
    /// the actual material before sending.
    /// </summary>
    public static Guid? ResolveDestinationDefaultId(AiModelRuntimeEntity? runtime, AiPurpose purpose) =>
        purpose == AiPurpose.NoteOrganization
            ? runtime?.NoteOrganizationModelId ?? runtime?.TextModelId ?? runtime?.VisionModelId
            : ResolveId(runtime, purpose);

    public static bool TryParse(string? value, out AiPurpose purpose)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            purpose = default;
            return false;
        }

        return Enum.TryParse(value, ignoreCase: true, out purpose) &&
            Enum.IsDefined(purpose);
    }

    public static string LogPurpose(AiPurpose purpose) => purpose switch
    {
        AiPurpose.NoteOrganization => "ai.note-organization",
        AiPurpose.NoteTodo => "ai.note-todo",
        AiPurpose.CalendarPlanning => "ai.calendar-planning",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown AI purpose")
    };

    public static string DisplayName(AiPurpose purpose) => purpose switch
    {
        AiPurpose.NoteOrganization => "笔记整理",
        AiPurpose.NoteTodo => "提取待办",
        AiPurpose.CalendarPlanning => "日历排程",
        _ => "AI"
    };
}
