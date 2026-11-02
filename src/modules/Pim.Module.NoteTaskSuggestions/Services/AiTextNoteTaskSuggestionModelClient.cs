using Pim.Core.Ai;
using Pim.Infrastructure.Ai;
using Pim.Module.NoteTaskSuggestions.Entities;

namespace Pim.Module.NoteTaskSuggestions.Services;

internal sealed class AiTextNoteTaskSuggestionModelClient(AiTextCallService calls)
    : INoteTaskSuggestionModelClient
{
    public async Task<NoteTaskSuggestionModelResult> ExtractAsync(
        Guid owner, NoteTaskSuggestionExtractionEntity extraction, CancellationToken ct)
    {
        var consent = new AiVisionDestinationConsent(
            extraction.ModelProfileId,
            extraction.ModelRevision,
            extraction.ModelEndpoint,
            Confirmed: true);
        var request = new AiTextCallRequest(
            extraction.Id,
            owner,
            NoteTaskSuggestionPrompt.BuildUserPrompt(extraction.SourceContentMarkdown),
            NoteTaskSuggestionPrompt.SystemPrompt,
            extraction.ModelProfileId);

        var result = await calls.CallForOwnerAsync(
            owner,
            request,
            consent,
            "note-derived-version",
            extraction.ArtifactVersionId.ToString("D"),
            "note.task-suggestion.extract",
            ct);
        return new(result.LogId, result.Status, result.ResponseText, result.ErrorMessage);
    }
}
