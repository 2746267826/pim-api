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
        Assert.Contains(transcripts[0].LogId!.Value.ToString("D"), record, StringComparison.Ordinal);
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

    [Fact]
    public void AudioContextIsAStableControlledSourceAndDoesNotRequireTranscripts()
    {
        var material = NoteDerivedFixture.CreateMaterial();
        var sourcePath = $"recordings/{material.RecordingId:D}/s0001-c000001.wav";
        var audio = new NoteDerivedAudioInput(0, material.RecordingId, 1, 1, 5, sourcePath,
            NoteDerivedFixture.Digest([4, 5, 6]), 0, 160, 364,
            $"recording-{material.RecordingId:D}-s0001-c000001-o000000000000-n160.wav");
        var snapshot = NoteDerivedFixture.Snapshot(material) with { Audio = [audio] };

        var plan = new NoteDerivedPlanner().Build(snapshot);
        var record = NoteDerivedPrompts.Record(snapshot, []);

        Assert.DoesNotContain(plan.InitialCoverage.Issues,
            issue => issue.Code.Contains("asr", StringComparison.Ordinal) ||
                issue.Code.StartsWith("audio_", StringComparison.Ordinal));
        Assert.Contains("服务端固定原始录音上下文", record, StringComparison.Ordinal);
        Assert.Contains(audio.SourceSha256, record, StringComparison.Ordinal);
        Assert.DoesNotContain(sourcePath, record, StringComparison.Ordinal);
    }

    [Fact]
    public void AudioContextOverTheSingleCallBudgetBlocksWithoutTruncation()
    {
        var material = NoteDerivedFixture.CreateMaterial();
        var audio = new NoteDerivedAudioInput(0, material.RecordingId, 1, 1, 5,
            $"recordings/{material.RecordingId:D}/s0001-c000001.wav",
            NoteDerivedFixture.Digest([4, 5, 6]), 0, NoteDerivedAudioContext.MaxBytes / 2,
            NoteDerivedAudioContext.ByteSize(NoteDerivedAudioContext.MaxBytes / 2), "recording-over-limit.wav");
        var snapshot = NoteDerivedFixture.Snapshot(material) with { Audio = [audio] };

        var plan = new NoteDerivedPlanner().Build(snapshot);

        Assert.Contains(plan.InitialCoverage.Issues, issue => issue.Code == "audio_slice_too_large");
        Assert.Contains("未截断", plan.InitialCoverage.Issues
            .Single(issue => issue.Code == "audio_slice_too_large").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LongAudioSplitsIntoOrderedBatchesAndKeepsOnlyOverlappingTranscripts()
    {
        var material = NoteDerivedFixture.CreateMaterial();
        const long samples = (1024 * 1024 - 44) / 2;
        var audio = Enumerable.Range(0, 9).Select(index => new NoteDerivedAudioInput(
            index, Guid.NewGuid(), 1, 1, index,
            $"recordings/recording-{index:D}.wav",
            NoteDerivedFixture.Digest([(byte)index]), 0, samples,
            NoteDerivedAudioContext.ByteSize(samples),
            $"recording-{index:D}-s0001-c000001-o000000000000-n{samples}.wav")).ToArray();
        var transcripts = audio.Select((slice, index) => new NoteDerivedTranscriptInput(
            index, index, slice.RecordingId, 1, 1, index, slice.Path, slice.SourceSha256,
            0, slice.Samples, $"Transcript only for batch slice {index}", Guid.NewGuid())).ToArray();
        var snapshot = NoteDerivedFixture.Snapshot(material, [], transcripts) with
        {
            Audio = audio
        };

        var plan = new NoteDerivedPlanner().Build(snapshot);

        Assert.Empty(plan.InitialCoverage.Issues);
        var batches = plan.Steps.Where(x => x.Kind == NoteDerivedStepKind.AudioBatchEvidence)
            .ToArray();
        Assert.Equal(new[] { "audio-batch:0001", "audio-batch:0002" },
            batches.Select(x => x.Key));
        Assert.Equal(new[] { 8, 1 }, batches.Select(x => x.Audio!.Count));
        Assert.Equal(audio.Select(x => x.Ordinal),
            batches.SelectMany(x => x.Audio!).Select(x => x.Ordinal));
        Assert.All(batches, batch =>
            Assert.True(batch.Audio!.Sum(x => x.SizeBytes) <= NoteDerivedAudioContext.MaxBytes));
        Assert.All(batches, batch => Assert.True(batch.MaxOutputCharacters >= 1000));
        var firstPrompt = NoteDerivedPrompts.AudioBatch(batches[0].Key, 0, 2,
            batches[0].Audio!, transcripts.Where(x => batches[0].TranscriptOrdinals!.Contains(x.Ordinal)));
        var secondPrompt = NoteDerivedPrompts.AudioBatch(batches[1].Key, 1, 2,
            batches[1].Audio!, transcripts.Where(x => batches[1].TranscriptOrdinals!.Contains(x.Ordinal)));
        Assert.Contains("Transcript only for batch slice 0", firstPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Transcript only for batch slice 8", firstPrompt,
            StringComparison.Ordinal);
        Assert.Contains("Transcript only for batch slice 8", secondPrompt, StringComparison.Ordinal);

        var record = plan.Steps.Single(x => x.Key == NoteDerivedPrompts.RecordKey);
        Assert.Equal(batches.Select(x => x.Key), record.DependsOn);
        Assert.Empty(record.Audio!);
        Assert.Equal(["audio-batch:0001", "audio-batch:0002"], record.DependsOn);
        Assert.Equal(["record-synthesis"],
            plan.Steps.Single(x => x.Key == NoteDerivedPrompts.AnalysisKey).DependsOn);
    }

    [Fact]
    public void TotalBudgetAndBatchLimitBlockBeforeAnyProviderCall()
    {
        var material = NoteDerivedFixture.CreateMaterial();
        const long samples = (1024 * 1024 - 44) / 2;
        var audio = Enumerable.Range(0, 9).Select(index => new NoteDerivedAudioInput(
            index, Guid.NewGuid(), 1, 1, index,
            $"recordings/recording-{index:D}.wav",
            NoteDerivedFixture.Digest([(byte)index]), 0, samples,
            NoteDerivedAudioContext.ByteSize(samples),
            $"recording-{index:D}.wav")).ToArray();
        var options = new NoteDerivedAudioContextOptions
        {
            MaxTotalBytes = 8 * 1024 * 1024,
            MaxBatches = 1
        };

        var plan = new NoteDerivedPlanner(options).Build(
            NoteDerivedFixture.Snapshot(material) with { Audio = audio });

        Assert.Contains(plan.InitialCoverage.Issues,
            issue => issue.Code == "audio_total_budget_exceeded");
        Assert.Contains(plan.InitialCoverage.Issues, issue => issue.Code == "audio_batch_limit");
        Assert.DoesNotContain(plan.Steps, step =>
            step.Kind == NoteDerivedStepKind.AudioBatchEvidence);
        Assert.False(plan.InitialCoverage.Complete);
    }

    [Fact]
    public void AudioEvidenceBudgetBelowThePerBatchFloorBlocksWithoutTruncation()
    {
        var material = NoteDerivedFixture.CreateMaterial();
        var audio = Enumerable.Range(0, 2).Select(index => new NoteDerivedAudioInput(
            index, Guid.NewGuid(), 1, 1, index, $"recordings/recording-{index:D}.wav",
            NoteDerivedFixture.Digest([(byte)index]), 0, 200_000,
            NoteDerivedAudioContext.ByteSize(200_000), $"recording-{index:D}.wav")).ToArray();
        var oversized = new string('a', NoteDerivedLimits.MaxSynthesisInputChars - 2_000);
        var snapshot = NoteDerivedFixture.Snapshot(material) with
        {
            NoteMarkdown = oversized,
            TextBlocks = [new(0, Guid.NewGuid(), oversized)],
            Audio = audio
        };

        var plan = new NoteDerivedPlanner(new NoteDerivedAudioContextOptions
        {
            MaxBatchBytes = 500_000
        }).Build(snapshot);

        Assert.Contains(plan.InitialCoverage.Issues,
            issue => issue.Code == "audio_evidence_budget_insufficient");
        Assert.False(plan.InitialCoverage.Complete);
    }

    [Fact]
    public void SliceAboveTheConfiguredBatchBudgetBlocksInsteadOfGrowingTheBatch()
    {
        var material = NoteDerivedFixture.CreateMaterial();
        var audio = new NoteDerivedAudioInput(0, Guid.NewGuid(), 1, 1, 0,
            "recordings/recording-0.wav", NoteDerivedFixture.Digest([7]), 0, 200_000,
            NoteDerivedAudioContext.ByteSize(200_000), "recording-0.wav");
        var plan = new NoteDerivedPlanner(new NoteDerivedAudioContextOptions
        {
            MaxBatchBytes = 200_000
        }).Build(NoteDerivedFixture.Snapshot(material) with { Audio = [audio] });

        Assert.Contains(plan.InitialCoverage.Issues,
            issue => issue.Code == "audio_batch_slice_too_large");
        Assert.DoesNotContain(plan.Steps, step =>
            step.Kind == NoteDerivedStepKind.AudioBatchEvidence);
        Assert.False(plan.InitialCoverage.Complete);
    }

    [Fact]
    public void TranscriptOutsideEveryAudioBatchBlocksInsteadOfBeingSilentlyOmitted()
    {
        var material = NoteDerivedFixture.CreateMaterial();
        var audio = Enumerable.Range(0, 2).Select(index => new NoteDerivedAudioInput(
            index, Guid.NewGuid(), 1, 1, index, $"recordings/recording-{index:D}.wav",
            NoteDerivedFixture.Digest([(byte)(index + 1)]), 0, 200_000,
            NoteDerivedAudioContext.ByteSize(200_000), $"recording-{index:D}.wav")).ToArray();
        var orphan = new NoteDerivedTranscriptInput(0, 0, Guid.NewGuid(), 1, 1, 0,
            "recordings/orphan.wav", NoteDerivedFixture.Digest([9]), 0, 200_000,
            "orphan transcript", Guid.NewGuid());
        var plan = new NoteDerivedPlanner(new NoteDerivedAudioContextOptions
        {
            MaxBatchBytes = 500_000
        }).Build(NoteDerivedFixture.Snapshot(material, [], [orphan]) with { Audio = audio });

        Assert.Contains(plan.InitialCoverage.Issues,
            issue => issue.Code == "audio_transcript_unassigned");
        Assert.DoesNotContain(plan.Steps, step =>
            step.Kind == NoteDerivedStepKind.AudioBatchEvidence);
        Assert.False(plan.InitialCoverage.Complete);
    }

    [Theory]
    [InlineData(0, 1, 1, 8 * 1024 * 1024)]
    [InlineData(1, 0, 1, 8 * 1024 * 1024)]
    [InlineData(1, 1, 0, 8 * 1024 * 1024)]
    [InlineData(1, 65, 1, 8 * 1024 * 1024)]
    [InlineData(1, 1, 5, 8 * 1024 * 1024)]
    [InlineData(1, 1, 1, 9 * 1024 * 1024)]
    public void AudioBatchOptionsRejectValuesOutsideHardCaps(
        long totalBytes, int batches, int parallel, int batchBytes)
    {
        var options = new NoteDerivedAudioContextOptions
        {
            MaxTotalBytes = totalBytes,
            MaxBatches = batches,
            MaxParallelCalls = parallel,
            MaxBatchBytes = batchBytes
        };

        Assert.False(options.IsValid());
    }
}
