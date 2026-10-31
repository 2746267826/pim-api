using Pim.Core.Notes;

namespace Pim.Infrastructure.Notes;

internal sealed record NoteDerivedActualImage(
    Guid LogId,
    int Index,
    string Name,
    string ContentType,
    long SizeBytes,
    string Sha256,
    bool Stored);

internal static class NoteDerivedCoverage
{
    internal static NoteDerivedCoverageDto Reconcile(
        NoteDerivedInputSnapshot input,
        IReadOnlyDictionary<string, NoteDerivedPlanStep> plannedSteps,
        IReadOnlyDictionary<string, NoteDerivedStepState> stepStates,
        IReadOnlyList<NoteDerivedActualImage> actualImages,
        IReadOnlySet<Guid> completedTranscriptLogIds,
        IReadOnlyList<NoteDerivedIssueDto> issues)
    {
        var batchKeys = plannedSteps.Values
            .Where(x => x.Kind == NoteDerivedStepKind.ImageEvidence)
            .Select(x => x.Key)
            .ToArray();
        var completedImages = new List<NoteDerivedImageInput>();
        var completedBatchKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in batchKeys)
        {
            if (!stepStates.TryGetValue(key, out var step) || step.Current.Status != "Succeeded" ||
                step.Current.LogId is not { } logId) continue;
            var planned = plannedSteps[key];
            var rows = actualImages.Where(image => image.LogId == logId)
                .OrderBy(image => image.Index).ToArray();
            var expected = planned.Images.OrderBy(image => image.Ordinal).ToArray();
            var rowsByIndex = rows.GroupBy(image => image.Index)
                .ToDictionary(group => group.Key, group => group.First());
            // Verified images count individually; the batch is complete only when every row matches exactly.
            var exact = rows.Length == expected.Length &&
                rowsByIndex.Count == rows.Length;
            for (var index = 0; index < expected.Length; index++)
            {
                var image = expected[index];
                if (!rowsByIndex.TryGetValue(index, out var row) || !row.Stored ||
                    row.Name != NoteDerivedPrompts.ImageName(image) ||
                    row.Sha256 != image.Sha256 || row.SizeBytes != image.SizeBytes ||
                    row.ContentType != image.ContentType)
                {
                    exact = false;
                    continue;
                }
                completedImages.Add(image);
            }
            if (exact) completedBatchKeys.Add(key);
        }

        var completedPhotos = completedImages.Count(image => image.Kind == "Photo");
        var completedInk = completedImages.Count(image => image.Kind == "InkPreview");
        var completedTranscripts = input.Transcripts.Count(x =>
            x.LogId is { } logId && completedTranscriptLogIds.Contains(logId));
        var completedBatches = completedBatchKeys.Count;

        var cleanIssues = DistinctIssues(issues);
        var complete = cleanIssues.Count == 0 &&
            completedTranscripts == input.Transcripts.Count &&
            completedPhotos == input.Images.Count(x => x.Kind == "Photo") &&
            completedInk == input.Images.Count(x => x.Kind == "InkPreview") &&
            completedBatches == batchKeys.Length;

        return new(
            input.TextBlocks.Count,
            input.Transcripts.Count,
            completedTranscripts,
            input.Images.Count(x => x.Kind == "Photo"),
            completedPhotos,
            input.Images.Count(x => x.Kind == "InkPreview"),
            completedInk,
            batchKeys.Length,
            completedBatches,
            complete,
            cleanIssues);
    }

    internal static List<NoteDerivedIssueDto> DistinctIssues(IEnumerable<NoteDerivedIssueDto> issues) =>
        issues.DistinctBy(x => (x.Code, x.Message, x.SourcePath, x.BlockId, x.StepKey)).ToList();
}
