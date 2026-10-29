using Pim.Core.Notes;
using Pim.Infrastructure.Notes;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteDerivedCoverageTests
{
    [Fact]
    public void CoverageUsesActualSuccessfulAiCallImagesRatherThanPlanOrManifest()
    {
        var material = NoteDerivedFixture.CreateMaterial(withAudio: false);
        var images = new[]
        {
            NoteDerivedFixture.Image(0, material.PhotoIndex, "Photo",
                NoteDerivedFixture.Digest(NoteDerivedFixture.PhotoBytes), NoteDerivedFixture.PhotoBytes.Length),
            NoteDerivedFixture.Image(1, material.InkIndex, "InkPreview",
                NoteDerivedFixture.Digest(NoteDerivedFixture.InkBytes), NoteDerivedFixture.InkBytes.Length)
        };
        var snapshot = NoteDerivedFixture.Snapshot(material, images);
        var plan = new NoteDerivedPlanner().Build(snapshot);
        var logId = Guid.NewGuid();
        var states = plan.Steps.ToDictionary(x => x.Key, x => new NoteDerivedStepState
        {
            Key = x.Key,
            Kind = x.Kind,
            DependsOn = x.DependsOn,
            Images = x.Images,
            Attempts = [new NoteDerivedAttemptState { Status = x.Key == "image-batch:0001" ? "Succeeded" : "Queued",
                LogId = x.Key == "image-batch:0001" ? logId : null }]
        }, StringComparer.Ordinal);

        var onlyPhoto = NoteDerivedCoverage.Reconcile(snapshot,
            plan.Steps.ToDictionary(x => x.Key, StringComparer.Ordinal), states,
            [new(logId, 0, NoteDerivedPrompts.ImageName(images[0]), images[0].ContentType,
                images[0].SizeBytes, images[0].Sha256, true)], new HashSet<Guid>(), []);
        Assert.Equal(1, onlyPhoto.CompletedPhotos);
        Assert.Equal(0, onlyPhoto.CompletedInkPreviews);
        Assert.Equal(0, onlyPhoto.CompletedVisionBatches);
        Assert.False(onlyPhoto.Complete);

        var actual = images.Select((image, index) => new NoteDerivedActualImage(logId, index,
            NoteDerivedPrompts.ImageName(image), image.ContentType, image.SizeBytes, image.Sha256,
            true)).ToArray();
        var complete = NoteDerivedCoverage.Reconcile(snapshot,
            plan.Steps.ToDictionary(x => x.Key, StringComparer.Ordinal), states,
            actual, new HashSet<Guid>(), []);
        Assert.Equal(1, complete.CompletedPhotos);
        Assert.Equal(1, complete.CompletedInkPreviews);
        Assert.Equal(1, complete.CompletedVisionBatches);
        Assert.True(complete.Complete);

        var tampered = actual.Select((image, index) => index == 1
            ? image with { Sha256 = new string('0', 64) } : image).ToArray();
        var rejected = NoteDerivedCoverage.Reconcile(snapshot,
            plan.Steps.ToDictionary(x => x.Key, StringComparer.Ordinal), states,
            tampered, new HashSet<Guid>(), []);
        Assert.Equal(0, rejected.CompletedInkPreviews);
        Assert.Equal(0, rejected.CompletedVisionBatches);
        Assert.False(rejected.Complete);

        var oldSuccessfulLog = Guid.NewGuid();
        var staleRows = actual.Select(image => image with { LogId = oldSuccessfulLog }).ToArray();
        var stale = NoteDerivedCoverage.Reconcile(snapshot,
            plan.Steps.ToDictionary(x => x.Key, StringComparer.Ordinal), states,
            staleRows, new HashSet<Guid>(), []);
        Assert.Equal(0, stale.CompletedPhotos);
        Assert.Equal(0, stale.CompletedInkPreviews);
        Assert.Equal(0, stale.CompletedVisionBatches);
        Assert.False(stale.Complete);
    }

    [Fact]
    public void TranscriptCoverageRequiresEverySuccessfulReceiptAndNoIssues()
    {
        var material = NoteDerivedFixture.CreateMaterial();
        var transcripts = new[]
        {
            new NoteDerivedTranscriptInput(0, 0, material.RecordingId, 1, 1, 5,
                $"recordings/{material.RecordingId:D}/s0001-c000001.wav",
                NoteDerivedFixture.Digest([7]), 0, 160, "Transcript", Guid.NewGuid())
        };
        var snapshot = NoteDerivedFixture.Snapshot(material, [], transcripts);
        var plan = new NoteDerivedPlanner().Build(snapshot);
        var states = plan.Steps.ToDictionary(x => x.Key, x => new NoteDerivedStepState
        {
            Key = x.Key,
            Kind = x.Kind,
            DependsOn = x.DependsOn,
            Images = x.Images,
            Attempts = [new NoteDerivedAttemptState { Status = "Queued" }]
        }, StringComparer.Ordinal);

        var missing = NoteDerivedCoverage.Reconcile(snapshot,
            plan.Steps.ToDictionary(x => x.Key, StringComparer.Ordinal), states, [],
            new HashSet<Guid>(), []);
        Assert.Equal(0, missing.CompletedTranscriptSegments);
        Assert.False(missing.Complete);

        var complete = NoteDerivedCoverage.Reconcile(snapshot,
            plan.Steps.ToDictionary(x => x.Key, StringComparer.Ordinal), states, [],
            new HashSet<Guid> { transcripts[0].LogId }, []);
        Assert.Equal(1, complete.CompletedTranscriptSegments);
        Assert.True(complete.Complete);
    }
}
