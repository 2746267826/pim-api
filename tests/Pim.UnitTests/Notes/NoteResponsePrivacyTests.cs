using Pim.Core.Notes;
using Xunit;

namespace Pim.UnitTests.Notes;

public class NoteResponsePrivacyTests
{
    private static readonly Guid ArtifactId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid VersionId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid TaskId = Guid.Parse("33333333-3333-4333-8333-333333333333");

    [Fact]
    public void OrdinaryUserVersionKeepsProtocolFieldsAndHidesDiagnostics()
    {
        var version = Version();

        var redacted = NoteResponsePrivacy.RedactVersion(version, includeDiagnostics: false);

        Assert.Equal(VersionId, redacted.VersionId);
        Assert.Equal(ArtifactId, redacted.ArtifactId);
        Assert.Equal(version.SourceVersionId, redacted.SourceVersionId);
        Assert.Equal(version.ContentSha256, redacted.ContentSha256);
        Assert.Equal(version.ContentMarkdown, redacted.ContentMarkdown);
        Assert.Equal(TaskId, redacted.GenerationTaskId);
        Assert.Equal("input-sha", redacted.InputSha256);
        Assert.Equal("manifest-sha", redacted.ManifestSha256);
        Assert.Equal(string.Empty, redacted.PlanVersion);
        Assert.Equal(string.Empty, redacted.PromptVersion);
        Assert.Null(redacted.AiCallLogId);
        Assert.Null(redacted.ModelProfileId);
        Assert.Null(redacted.ModelRevision);
        Assert.Null(redacted.ModelEndpoint);
        var issue = Assert.Single(redacted.Coverage!.Issues);
        Assert.Equal(NoteResponsePrivacy.GenericIssueCode, issue.Code);
        Assert.Null(issue.SourcePath);
        Assert.Null(issue.StepKey);
    }

    [Fact]
    public void OrdinaryUserArtifactKeepsVersionIdsAndGenerationTask()
    {
        var artifact = new NoteDerivedArtifactDto(ArtifactId, Guid.NewGuid(), NoteDerivedArtifactKind.CompleteRecord,
            Guid.NewGuid(), VersionId,
            [
                new NoteDerivedVersionSummaryDto(VersionId, NoteDerivedVersionAuthor.Model, Guid.NewGuid(),
                    null, null, TaskId, new string('c', 64), false, DateTimeOffset.UnixEpoch, true)
            ]);

        var redacted = NoteResponsePrivacy.RedactArtifact(artifact, includeDiagnostics: false);

        var summary = Assert.Single(redacted.Versions);
        Assert.Equal(VersionId, summary.VersionId);
        Assert.Equal(summary.ContentSha256, new string('c', 64));
        Assert.Equal(TaskId, summary.GenerationTaskId);
    }

    [Fact]
    public void OrdinaryUserTaskKeepsSyncFieldsAndHidesAttemptDiagnostics()
    {
        var issue = Issue();
        var task = new NoteDerivedTaskDto(TaskId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
            null, "input-sha", "manifest-sha", "Running",
            new NoteDerivedGenerationConsent(Guid.NewGuid(), Guid.NewGuid(),
                "https://internal.example/v1/", true),
            new NoteDerivedCoverageDto(1, 0, 0, 1, 0, 0, 0, 1, 0, false, [issue]),
            [new NoteDerivedArtifactRefDto(ArtifactId, NoteDerivedArtifactKind.CompleteRecord, VersionId)],
            [issue],
            [
                new NoteDerivedStepDto("image-batch:0001", NoteDerivedStepKind.ImageEvidence, "Failed",
                    [],
                    [new NoteDerivedAttemptDto(Guid.NewGuid(), Guid.NewGuid(), "Failed",
                        "provider_error", "https://internal.example/v1 返回 500")])
            ],
            "plan-v1", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false);

        var redacted = NoteResponsePrivacy.RedactTask(task, includeDiagnostics: false);

        Assert.Equal(TaskId, redacted.TaskId);
        Assert.Equal(task.Revision, redacted.Revision);
        Assert.Equal("input-sha", redacted.InputSha256);
        Assert.Equal("manifest-sha", redacted.ManifestSha256);
        Assert.Equal(task.Destination, redacted.Destination);
        Assert.Equal(ArtifactId, Assert.Single(redacted.Artifacts).ArtifactId);
        Assert.Equal(string.Empty, redacted.PlanVersion);
        Assert.Equal(NoteResponsePrivacy.GenericIssueCode, Assert.Single(redacted.Issues).Code);
        var step = Assert.Single(redacted.Steps);
        Assert.Equal("image-batch:0001", step.Key);
        var attempt = Assert.Single(step.Attempts);
        Assert.Equal(Guid.Empty, attempt.RequestId);
        Assert.Null(attempt.LogId);
        Assert.Null(attempt.ErrorCode);
        Assert.Null(attempt.ErrorMessage);
    }

    [Fact]
    public void OrdinaryUserReceiptKeepsProtocolIdentifiersAndHidesDiagnostics()
    {
        var issue = Issue();
        var binding = new NoteDerivedReceiptBindingDto("VisionImage", "image-batch:0001",
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Verified", "note_derived_task",
            TaskId.ToString("D"), "ai_request_log", Guid.NewGuid().ToString("D"),
            "expected-input", "actual-input", "expected-output", "actual-output", true);
        var receipt = new NoteDerivedTaskReceiptDto(TaskId, Guid.NewGuid(), Guid.NewGuid(), null,
            "input-sha", "manifest-sha", "NeedsAttention", false,
            [
                new NoteDerivedStepReceiptDto("image-batch:0001", NoteDerivedStepKind.ImageEvidence,
                    "Consumed", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), binding, [issue])
            ],
            [
                new NoteDerivedSourceReceiptDto("image:0001", NoteDerivedSourceKind.Photo, 0,
                    Guid.NewGuid(), "assets/private/secret.png", new string('a', 64),
                    new string('b', 64), 42, null, "asr-receipt", "Consumed", [binding], [issue])
            ],
            [
                new NoteDerivedBatchReceiptDto("image-batch:0001", "Consumed", ["image:0001"],
                    1, 1, binding, [issue])
            ],
            [issue]);

        var redacted = NoteResponsePrivacy.RedactReceipt(receipt, includeDiagnostics: false);

        Assert.Equal(string.Empty, redacted.InputSha256);
        Assert.Equal(string.Empty, redacted.ManifestSha256);
        var step = Assert.Single(redacted.Steps);
        Assert.Equal("image-batch:0001", step.StepKey);
        Assert.Null(step.RequestId);
        Assert.Null(step.AiCallLogId);
        Assert.Null(step.EvidenceId);
        Assert.Null(step.Binding);
        var source = Assert.Single(redacted.Sources);
        Assert.Equal("image:0001", source.SourceId);
        Assert.Equal(NoteDerivedSourceKind.Photo, source.Kind);
        Assert.Null(source.BlockId);
        Assert.Null(source.SourcePath);
        Assert.Null(source.SourceSha256);
        Assert.Null(source.TextSha256);
        Assert.Null(source.SizeBytes);
        Assert.Null(source.AsrReceiptKind);
        Assert.Empty(source.Bindings);
        var batch = Assert.Single(redacted.Batches);
        Assert.Equal("image-batch:0001", batch.StepKey);
        Assert.Equal("image:0001", Assert.Single(batch.SourceIds));
        Assert.Equal(1, batch.PlannedImages);
        Assert.Equal(1, batch.ActualImages);
        Assert.Null(batch.Binding);
        Assert.Equal(NoteResponsePrivacy.GenericIssueCode, Assert.Single(source.Issues).Code);
        Assert.Equal(NoteResponsePrivacy.GenericIssueCode, Assert.Single(batch.Issues).Code);
    }

    [Fact]
    public void AdminVersionAndArtifactKeepDiagnostics()
    {
        var version = Version();
        var artifact = new NoteDerivedArtifactDto(ArtifactId, Guid.NewGuid(), NoteDerivedArtifactKind.CompleteRecord,
            Guid.NewGuid(), VersionId,
            [
                new NoteDerivedVersionSummaryDto(VersionId, NoteDerivedVersionAuthor.Model, Guid.NewGuid(),
                    null, null, TaskId, new string('c', 64), false, DateTimeOffset.UnixEpoch, true)
            ]);

        var keptVersion = NoteResponsePrivacy.RedactVersion(version, includeDiagnostics: true);
        var keptArtifact = NoteResponsePrivacy.RedactArtifact(artifact, includeDiagnostics: true);

        Assert.Equal("input-sha", keptVersion.InputSha256);
        Assert.Equal("manifest-sha", keptVersion.ManifestSha256);
        Assert.Equal("plan-v1", keptVersion.PlanVersion);
        Assert.Equal("prompt-v1", keptVersion.PromptVersion);
        Assert.Equal(TaskId, keptVersion.GenerationTaskId);
        Assert.Equal("https://internal.example/v1/", keptVersion.ModelEndpoint);
        Assert.Equal("markdown_unreadable", Assert.Single(keptVersion.Coverage!.Issues).Code);
        Assert.Equal(TaskId, Assert.Single(keptArtifact.Versions).GenerationTaskId);
    }

    private static NoteDerivedVersionDto Version() => new(
        VersionId, ArtifactId, NoteDerivedArtifactKind.CompleteRecord, NoteDerivedVersionAuthor.Model,
        null, null, null, Guid.NewGuid(), TaskId, "正文", new string('c', 64), "input-sha", "manifest-sha",
        "plan-v1", "prompt-v1", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "https://internal.example/v1/",
        new NoteDerivedCoverageDto(1, 0, 0, 0, 0, 0, 0, 0, 0, false,
            [new NoteDerivedIssueDto("markdown_unreadable", "读取 /data/private/secret.md 失败",
                "private/secret.md", Guid.NewGuid().ToString("D"), "image-batch:0001")]),
        true, DateTimeOffset.UnixEpoch, false);

    private static NoteDerivedIssueDto Issue() => new("markdown_unreadable",
        "读取 /data/private/secret.md 失败", "private/secret.md", Guid.NewGuid().ToString("D"),
        "image-batch:0001");
}
