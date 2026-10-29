using Microsoft.Extensions.Options;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Notes;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteDerivedStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pim-note-derived-store",
        Guid.NewGuid().ToString("N"));
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _noteId = Guid.NewGuid();
    private readonly Guid _sourceVersionId = Guid.NewGuid();
    private NoteDerivedStore Store => new(Options.Create(new NoteMaterialUploadOptions { StorageRoot = _root }));

    [Fact]
    public async Task GeneratedVersionsNeverAdvanceHead_AndManualCasPreservesBothSides()
    {
        var store = Store;
        var artifact = await store.EnsureArtifactAsync(_owner, _noteId,
            NoteDerivedArtifactKind.CompleteRecord, default);
        var same = await store.EnsureArtifactAsync(_owner, _noteId,
            NoteDerivedArtifactKind.CompleteRecord, default);
        Assert.Equal(artifact.ArtifactId, same.ArtifactId);

        var generated = Guid.NewGuid();
        await store.AppendGeneratedAsync(_owner, artifact.ArtifactId,
            Draft(generated, "模型完整记录"), default);
        var afterGenerated = await store.GetArtifactAsync(_owner, artifact.ArtifactId, default);
        Assert.Null(afterGenerated.HeadVersionId);

        var selected = await store.SelectHeadAsync(_owner, artifact.ArtifactId,
            new(afterGenerated.HeadRevision, generated), default);
        Assert.Equal(generated, selected.HeadVersionId);

        var manualA = Guid.NewGuid();
        var requestA = new SaveNoteDerivedManualRevisionRequest(
            manualA, selected.Revision, selected.HeadVersionId, "人工完整记录 A");
        var matched = await store.AppendManualAsync(_owner, artifact.ArtifactId, requestA, default);
        Assert.False(matched.HasConflict);
        Assert.True(matched.IsHead);
        Assert.Equal(manualA, matched.Artifact.HeadVersionId);

        var staleRevision = selected.Revision;
        var staleVersion = selected.HeadVersionId;
        var headRevision = matched.Artifact.Revision;
        var manualB = Guid.NewGuid();
        var requestB = new SaveNoteDerivedManualRevisionRequest(
            manualB, staleRevision, staleVersion, "并发人工完整记录 B");
        var conflicted = await store.AppendManualAsync(_owner, artifact.ArtifactId, requestB, default);
        Assert.True(conflicted.HasConflict);
        Assert.False(conflicted.IsHead);
        Assert.Equal(manualA, conflicted.Artifact.HeadVersionId);
        Assert.Equal(headRevision, conflicted.Artifact.Revision);

        var replay = await store.AppendManualAsync(_owner, artifact.ArtifactId, requestB, default);
        Assert.True(replay.HasConflict);
        Assert.False(replay.IsHead);
        Assert.Equal(manualB, replay.VersionId);
        Assert.Equal(manualA, replay.Artifact.HeadVersionId);

        var candidate = await store.GetVersionAsync(_owner, artifact.ArtifactId, manualB, default);
        Assert.Equal(manualA, candidate.ObservedHeadVersionId);
        Assert.Equal("并发人工完整记录 B", candidate.ContentMarkdown);
        Assert.True(candidate.HasConflict);

        var selectedB = await store.SelectHeadAsync(_owner, artifact.ArtifactId,
            new(conflicted.Artifact.Revision, manualB), default);
        Assert.Equal(manualB, selectedB.HeadVersionId);
        var replayA = await store.AppendManualAsync(_owner, artifact.ArtifactId, requestA, default);
        Assert.False(replayA.HasConflict);
        Assert.False(replayA.IsHead);
        Assert.Equal(manualB, replayA.Artifact.HeadVersionId);

        var error = await Assert.ThrowsAsync<DomainException>(() => store.AppendManualAsync(
            _owner, artifact.ArtifactId,
            requestB with { ContentMarkdown = "同一修订标识的另一份内容" }, default));
        Assert.Equal(40901, error.ErrorCode);
        var baseDrift = await Assert.ThrowsAsync<DomainException>(() => store.AppendManualAsync(
            _owner, artifact.ArtifactId,
            requestA with { ExpectedHeadRevision = Guid.NewGuid() }, default));
        Assert.Equal(40901, baseDrift.ErrorCode);
    }

    [Fact]
    public async Task TaskReplayAndStopAreDurable_AndTaskSummaryShowsOnlyFirstTenSteps()
    {
        var store = Store;
        var plan = Plan(12);
        var taskId = Guid.NewGuid();
        var request = new StartNoteDerivedTaskRequest(taskId, _owner, _sourceVersionId, null,
            new(Guid.NewGuid(), Guid.NewGuid(), "https://synthetic.invalid/v1/", true));

        var first = await store.CreateTaskAsync(_owner, request, plan, "fingerprint-a", default);
        var replay = await store.CreateTaskAsync(_owner, request, plan, "fingerprint-a", default);
        Assert.Equal(first.Revision, replay.Revision);
        Assert.Equal(10, NoteDerivedStore.DescribeTask(first).Steps.Count);
        Assert.Equal(12, first.Steps.Count);

        var conflict = await Assert.ThrowsAsync<DomainException>(() =>
            store.CreateTaskAsync(_owner, request, plan, "fingerprint-b", default));
        Assert.Equal(40901, conflict.ErrorCode);

        var stopped = await store.UpdateTaskAsync(_owner, taskId, state => state.StopRequested = true, default);
        Assert.True(stopped.StopRequested);
        Assert.Equal("Stopped", NoteDerivedStore.DescribePhase(stopped));
        var noOp = await store.UpdateTaskAsync(_owner, taskId, state => state.StopRequested = true, default);
        Assert.Equal(stopped.Revision, noOp.Revision);
    }

    [Fact]
    public async Task MissingLocatorIsRepairedFromAuthoritativeArtifactOnEnsureAndByIdRead()
    {
        var store = Store;
        var artifact = await store.EnsureArtifactAsync(_owner, _noteId,
            NoteDerivedArtifactKind.CompleteRecord, default);
        var locator = Path.Combine(_root, "derived", _owner.ToString("D"), "artifact-index",
            artifact.ArtifactId.ToString("D") + ".json");
        Assert.True(File.Exists(locator));
        File.Delete(locator);

        var repaired = await store.EnsureArtifactAsync(_owner, _noteId,
            NoteDerivedArtifactKind.CompleteRecord, default);
        Assert.Equal(artifact.ArtifactId, repaired.ArtifactId);
        Assert.True(File.Exists(locator));

        File.Delete(locator);
        var byId = await store.GetArtifactAsync(_owner, artifact.ArtifactId, default);
        Assert.Equal(artifact.ArtifactId, byId.ArtifactId);
        Assert.True(File.Exists(locator));
    }

    [Fact]
    public async Task StaleSelectDoesNotMoveHeadAndCannotOverwriteArtifactRevision()
    {
        var store = Store;
        var artifact = await store.EnsureArtifactAsync(_owner, _noteId,
            NoteDerivedArtifactKind.IndependentAnalysis, default);
        var firstVersion = Guid.NewGuid();
        var firstDraft = Draft(firstVersion, "分析一", NoteDerivedArtifactKind.IndependentAnalysis);
        await store.AppendGeneratedAsync(_owner, artifact.ArtifactId, firstDraft, default);
        var sourceDrift = await Assert.ThrowsAsync<DomainException>(() => store.AppendGeneratedAsync(
            _owner, artifact.ArtifactId,
            firstDraft with { SourceVersionId = Guid.NewGuid() }, default));
        Assert.Equal(40901, sourceDrift.ErrorCode);
        var initial = await store.GetArtifactAsync(_owner, artifact.ArtifactId, default);
        var selected = await store.SelectHeadAsync(_owner, artifact.ArtifactId,
            new(initial.HeadRevision, firstVersion), default);

        var secondVersion = Guid.NewGuid();
        await store.AppendGeneratedAsync(_owner, artifact.ArtifactId,
            Draft(secondVersion, "分析二", NoteDerivedArtifactKind.IndependentAnalysis), default);
        var error = await Assert.ThrowsAsync<DomainException>(() => store.SelectHeadAsync(
            _owner, artifact.ArtifactId, new(initial.HeadRevision, secondVersion), default));
        Assert.Equal(40901, error.ErrorCode);

        var unchanged = await store.GetArtifactAsync(_owner, artifact.ArtifactId, default);
        Assert.Equal(selected.HeadVersionId, unchanged.HeadVersionId);
        Assert.Equal(selected.Revision, unchanged.HeadRevision);
    }

    private NoteDerivedPlan Plan(int stepCount)
    {
        var input = new NoteDerivedInputSnapshot(_owner, _noteId, _sourceVersionId, null, null,
            "input-sha", "manifest-sha", "合成笔记", "正文",
            [new(0, Guid.NewGuid(), "正文")], [], [], []);
        var coverage = new NoteDerivedCoverageDto(1, 0, 0, 0, 0, 0, 0, 0, 0, true, []);
        var steps = Enumerable.Range(0, stepCount).Select(index => new NoteDerivedPlanStep(
            $"image-batch:{index + 1:D4}", NoteDerivedStepKind.ImageEvidence, [], [], 1)).ToArray();
        return new(NoteDerivedLimits.PlanVersion, input, steps, coverage);
    }

    private NoteDerivedVersionDraft Draft(Guid versionId, string content,
        NoteDerivedArtifactKind kind = NoteDerivedArtifactKind.CompleteRecord) => new(
        versionId,
        kind,
        null,
        null,
        _sourceVersionId,
        null,
        content,
        "input-sha",
        "manifest-sha",
        NoteDerivedLimits.PlanVersion,
        "prompt-v1",
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "https://synthetic.invalid/v1/",
        new NoteDerivedCoverageDto(1, 0, 0, 1, 1, 1, 1, 1, 1, true, []));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
