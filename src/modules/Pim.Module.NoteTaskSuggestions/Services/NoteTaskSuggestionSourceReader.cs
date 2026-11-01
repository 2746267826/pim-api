using Pim.Infrastructure.Notes;

namespace Pim.Module.NoteTaskSuggestions.Services;

internal sealed record NoteTaskSuggestionSource(
    Guid NoteId,
    Guid ArtifactId,
    Guid ArtifactVersionId,
    string ContentMarkdown,
    string ContentSha256,
    string InputSha256,
    string ManifestSha256,
    Guid? GenerationTaskId);

internal interface INoteTaskSuggestionSourceReader
{
    Task<NoteTaskSuggestionSource> ReadAsync(Guid artifactId, Guid versionId, CancellationToken ct);
}

internal sealed class DerivedNoteTaskSuggestionSourceReader(NoteDerivedService derived)
    : INoteTaskSuggestionSourceReader
{
    public async Task<NoteTaskSuggestionSource> ReadAsync(
        Guid artifactId, Guid versionId, CancellationToken ct)
    {
        var version = await derived.GetVersionAsync(artifactId, versionId, ct);
        var artifact = await derived.GetArtifactAsync(artifactId, ct);
        return new(
            artifact.NoteId,
            version.ArtifactId,
            version.VersionId,
            version.ContentMarkdown,
            version.ContentSha256,
            version.InputSha256,
            version.ManifestSha256,
            version.GenerationTaskId);
    }
}
