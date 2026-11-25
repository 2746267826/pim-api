namespace Pim.Core.Notes;

/// <summary>
/// Projects successful Native Notes responses for ordinary users.
///
/// Version/artifact/task identifiers, CAS revisions, step keys, generation task ids and the fixed
/// input/manifest/content hashes stay in the response because the Android/Windows upload, task
/// suggestion, conflict-resolution and bundle protocols need them. Diagnostics that are not part
/// of those protocols (attempt/log ids, error text, source paths, prompt/model metadata, receipt
/// bindings and coverage issue details) are removed.
///
/// The material endpoints (<c>/native-notes/{noteId}/current</c>, <c>/versions/{id}</c>,
/// <c>/versions/{id}/bundle</c> and <c>/{noteId}/versions</c>) intentionally keep the owner's
/// <c>InputJson</c>/<c>ManifestJson</c>: they are the user-authored note and its resumable upload
/// contract, not server diagnostics.
/// </summary>
public static class NoteResponsePrivacy
{
    public const string GenericIssueCode = "note_issue";
    public const string GenericIssueMessage = "笔记处理记录需要核对，请刷新后重试";

    public static NoteDerivedTaskDto RedactTask(NoteDerivedTaskDto task, bool includeDiagnostics)
    {
        if (includeDiagnostics) return task;
        return task with
        {
            PlanVersion = string.Empty,
            Coverage = RedactCoverage(task.Coverage),
            Issues = RedactIssues(task.Issues),
            Steps = task.Steps.Select(step => RedactStep(step)).ToArray()
        };
    }

    public static NoteDerivedStepDto RedactStep(NoteDerivedStepDto step, bool includeDiagnostics = false)
    {
        if (includeDiagnostics) return step;
        return step with
        {
            Attempts = step.Attempts.Select(attempt => attempt with
            {
                RequestId = Guid.Empty,
                LogId = null,
                ErrorCode = null,
                ErrorMessage = null
            }).ToArray()
        };
    }

    public static NoteDerivedStepsDto RedactSteps(NoteDerivedStepsDto steps, bool includeDiagnostics)
    {
        if (includeDiagnostics) return steps;
        return steps with { Items = steps.Items.Select(step => RedactStep(step)).ToArray() };
    }

    public static NoteDerivedArtifactDto RedactArtifact(NoteDerivedArtifactDto artifact, bool includeDiagnostics)
    {
        // Version summaries contain only ids, hashes, conflict/head state and timestamps.
        // GenerationTaskId is retained because task suggestions and Android bind to it.
        return artifact;
    }

    public static NoteDerivedVersionDto RedactVersion(NoteDerivedVersionDto version, bool includeDiagnostics)
    {
        if (includeDiagnostics) return version;
        return version with
        {
            PlanVersion = string.Empty,
            PromptVersion = string.Empty,
            AiCallLogId = null,
            ModelProfileId = null,
            ModelRevision = null,
            ModelEndpoint = null,
            Coverage = version.Coverage is null ? null : RedactCoverage(version.Coverage)
        };
    }

    public static NoteDerivedMutationDto RedactMutation(NoteDerivedMutationDto mutation, bool includeDiagnostics)
    {
        if (includeDiagnostics) return mutation;
        return mutation with { Artifact = RedactArtifact(mutation.Artifact, includeDiagnostics: false) };
    }

    public static NoteDerivedTaskReceiptDto RedactReceipt(NoteDerivedTaskReceiptDto receipt,
        bool includeDiagnostics)
    {
        if (includeDiagnostics) return receipt;
        return receipt with
        {
            InputSha256 = string.Empty,
            ManifestSha256 = string.Empty,
            Issues = RedactIssues(receipt.Issues),
            Steps = receipt.Steps.Select(step => step with
            {
                RequestId = null,
                AiCallLogId = null,
                EvidenceId = null,
                Binding = null,
                Issues = RedactIssues(step.Issues)
            }).ToArray(),
            Sources = receipt.Sources.Select(source => source with
            {
                BlockId = null,
                SourcePath = null,
                SourceSha256 = null,
                TextSha256 = null,
                SizeBytes = null,
                TextCharacters = null,
                AsrReceiptKind = null,
                Bindings = [],
                Issues = RedactIssues(source.Issues)
            }).ToArray(),
            Batches = receipt.Batches.Select(batch => batch with
            {
                Binding = null,
                Issues = RedactIssues(batch.Issues)
            }).ToArray()
        };
    }

    private static NoteDerivedCoverageDto RedactCoverage(NoteDerivedCoverageDto coverage) =>
        coverage with { Issues = RedactIssues(coverage.Issues) };

    private static IReadOnlyList<NoteDerivedIssueDto> RedactIssues(
        IReadOnlyList<NoteDerivedIssueDto> issues) =>
        issues.Select(_ => new NoteDerivedIssueDto(GenericIssueCode, GenericIssueMessage)).ToArray();
}
