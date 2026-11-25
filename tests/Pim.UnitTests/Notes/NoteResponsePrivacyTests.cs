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
        Assert.Null(redacted.GenerationTaskId);
        Assert.Equal(string.Empty, redacted.InputSha256);
        Assert.Equal(string.Empty, redacted.ManifestSha256);
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
    public void OrdinaryUserArtifactKeepsVersionIdsAndHidesGenerationTask()
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
        Assert.Null(summary.GenerationTaskId);
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
}
