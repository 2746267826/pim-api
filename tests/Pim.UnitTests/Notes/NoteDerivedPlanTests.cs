using Pim.Core.Notes;
using Pim.Infrastructure.Notes;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteDerivedPlanTests
{
    [Fact]
    public void AllImagesAreKeptInStableSdkBatchesAndSynthesisKeysAreExact()
    {
        var material = NoteDerivedFixture.CreateMaterial(withAudio: false);
        var images = Enumerable.Range(0, 9).Select(index => NoteDerivedFixture.Image(
            index, index + 2, index % 2 == 0 ? "Photo" : "InkPreview",
            NoteDerivedFixture.Digest([(byte)index]), 2 * 1024 * 1024)).ToArray();
        var plan = new NoteDerivedPlanner().Build(NoteDerivedFixture.Snapshot(material, images));

        Assert.Equal(new[]
        {
            "image-batch:0001", "image-batch:0002", "image-batch:0003",
            "record-synthesis", "analysis-synthesis"
        }, plan.Steps.Select(x => x.Key));
        Assert.Equal(new[] { 4, 4, 1 }, plan.Steps.Take(3).Select(x => x.Images.Count));
        Assert.Equal(9, plan.Steps.Take(3).SelectMany(x => x.Images).Select(x => x.Ordinal)
            .Distinct().Count());
        Assert.Equal(["image-batch:0001", "image-batch:0002", "image-batch:0003"],
            plan.Steps.Single(x => x.Key == "record-synthesis").DependsOn);
        Assert.Equal(["record-synthesis"],
            plan.Steps.Single(x => x.Key == "analysis-synthesis").DependsOn);
        Assert.Equal(9, plan.InitialCoverage.Photos + plan.InitialCoverage.InkPreviews);
        Assert.Equal(3, plan.InitialCoverage.VisionBatches);
        Assert.Empty(plan.InitialCoverage.Issues);
    }

    [Fact]
    public void WebpAndOversizedImagesBlockWithoutTranscodeOrTruncation()
    {
        var material = NoteDerivedFixture.CreateMaterial(withAudio: false);
        var images = new[]
        {
            NoteDerivedFixture.Image(0, 2, "Photo", NoteDerivedFixture.Digest([1]), 128,
                "assets/photo.webp") with { ContentType = "image/webp" },
            NoteDerivedFixture.Image(1, 3, "InkPreview", NoteDerivedFixture.Digest([2]),
                NoteDerivedLimits.MaxImageBytes + 1)
        };
        var plan = new NoteDerivedPlanner().Build(NoteDerivedFixture.Snapshot(material, images));

        Assert.Contains(plan.InitialCoverage.Issues, x => x.Code == "image_webp_unsupported");
        Assert.Contains(plan.InitialCoverage.Issues, x => x.Code == "image_size_limit");
        Assert.DoesNotContain(plan.Steps, x => x.Kind == NoteDerivedStepKind.ImageEvidence);
        Assert.Equal(2, plan.InitialCoverage.Photos + plan.InitialCoverage.InkPreviews);
        Assert.False(plan.InitialCoverage.Complete);
    }

    [Fact]
    public void SynthesisOverActualSdkLimitStopsAndNeverSubstringsInput()
    {
        var material = NoteDerivedFixture.CreateMaterial(withAudio: false);
        var oversized = new string('a', NoteDerivedLimits.MaxSynthesisInputChars) + "UNIQUE_TAIL";
        var snapshot = NoteDerivedFixture.Snapshot(material) with
        {
            NoteMarkdown = oversized,
            TextBlocks = [new(0, Guid.NewGuid(), oversized)]
        };
        var plan = new NoteDerivedPlanner().Build(snapshot);
        var prompt = NoteDerivedPrompts.Record(snapshot, []);

        Assert.Contains(plan.InitialCoverage.Issues, x => x.Code == "record_input_too_large");
        Assert.True(prompt.Length > NoteDerivedLimits.MaxSynthesisInputChars);
        Assert.Contains("UNIQUE_TAIL", prompt, StringComparison.Ordinal);
        Assert.True(plan.Steps.Single(x => x.Key == "record-synthesis").InputCharacters >
            NoteDerivedLimits.MaxSynthesisInputChars);
    }

    [Fact]
    public void TwoSynthesisStepsUseDifferentInstructionsAndIncludeEveryTranscriptReceiptIdentity()
    {
        var material = NoteDerivedFixture.CreateMaterial();
        var transcripts = new[]
        {
            new NoteDerivedTranscriptInput(0, 0, material.RecordingId, 1, 1, 5,
                $"recordings/{material.RecordingId:D}/s0001-c000001.wav",
                NoteDerivedFixture.Digest([1, 2, 3]), 0, 160, "Full transcript text", Guid.NewGuid())
        };
        var snapshot = NoteDerivedFixture.Snapshot(material, transcripts: transcripts);
        var record = NoteDerivedPrompts.Record(snapshot, []);
        var analysis = NoteDerivedPrompts.Analysis(snapshot, Guid.NewGuid(), "Validated complete record", []);

        Assert.NotEqual(record, analysis);
        Assert.Contains("Full transcript text", record, StringComparison.Ordinal);
        Assert.Contains(transcripts[0].LogId.ToString("D"), record, StringComparison.Ordinal);
        Assert.Contains(transcripts[0].SourceSha256, record, StringComparison.Ordinal);
        Assert.Contains("完整记录整理器", record, StringComparison.Ordinal);
        Assert.Contains("独立分析器", analysis, StringComparison.Ordinal);
        Assert.Contains("Validated complete record", analysis, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleAsrSlicesFromOneLongWaveChunkRemainDistinctBySegmentIndexAndOffsets()
    {
        var material = NoteDerivedFixture.CreateMaterial();
        var path = $"recordings/{material.RecordingId:D}/s0001-c000001.wav";
        var sha = NoteDerivedFixture.Digest([9, 8, 7]);
        var transcripts = new[]
        {
            new NoteDerivedTranscriptInput(0, 0, material.RecordingId, 1, 1, 5, path, sha,
                0, 960_000, "First minute", Guid.NewGuid()),
            new NoteDerivedTranscriptInput(1, 1, material.RecordingId, 1, 1, 5, path, sha,
                960_000, 42, "Second slice", Guid.NewGuid())
        };
        var plan = new NoteDerivedPlanner().Build(NoteDerivedFixture.Snapshot(material, [], transcripts));

        Assert.DoesNotContain(plan.InitialCoverage.Issues,
            x => x.Code.Contains("duplicate", StringComparison.Ordinal));
        Assert.Equal(2, plan.InitialCoverage.TranscriptSegments);
    }
}
