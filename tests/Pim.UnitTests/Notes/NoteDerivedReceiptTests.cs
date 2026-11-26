using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pim.Core.Ai;
using Pim.Core.Notes;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Notes;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteDerivedReceiptTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ReceiptBindsEveryPlannedSourceToTheActualFixedCallAndResult()
    {
        var fixture = CreateCompleteFixture();

        var receipt = NoteDerivedReceipts.Build(fixture.State, fixture.ActualImages,
            new HashSet<Guid>(), fixture.Context, []);

        Assert.True(receipt.Complete);
        Assert.Equal(3, receipt.Sources.Count);
        Assert.All(receipt.Sources, source => Assert.Equal("Consumed", source.Status));
        Assert.Single(receipt.Batches);
        Assert.Equal("Consumed", receipt.Batches[0].Status);
        Assert.Equal(2, receipt.Batches[0].ActualImages);
        Assert.Contains(receipt.Sources, x => x.Kind == NoteDerivedSourceKind.Photo &&
            x.Bindings.Any(binding => binding.Stage == "VisionImage" &&
                binding.AiCallLogId == fixture.VisionLog.Id && binding.Verified));
        Assert.Contains(receipt.Sources, x => x.Kind == NoteDerivedSourceKind.InkPreview &&
            x.Bindings.Any(binding => binding.Stage == "VisionImage" &&
                binding.AiCallLogId == fixture.VisionLog.Id && binding.Verified));
        var serialized = JsonSerializer.Serialize(receipt);
        Assert.DoesNotContain(fixture.Markdown, serialized);
        Assert.DoesNotContain(fixture.VisionResponse, serialized);
    }

    [Fact]
    public void WrongClientRequestInputAndOutputAreExplicitGaps()
    {
        var fixture = CreateCompleteFixture();
        fixture.VisionLog.ClientRequestId = Guid.NewGuid();
        fixture.VisionLog.UserId = Guid.NewGuid();
        fixture.VisionLog.Purpose = "wrong-purpose";
        fixture.VisionLog.SourceObjectId = Guid.NewGuid().ToString("D");
        fixture.VisionLog.InputHash = "wrong-input";
        fixture.VisionLog.OutputHash = "wrong-output";
        var context = fixture.Context with
        {
            LogsById = new Dictionary<Guid, AiRequestLogEntity>
            {
                [fixture.VisionLog.Id] = fixture.VisionLog
            }
        };

        var receipt = NoteDerivedReceipts.Build(fixture.State, fixture.ActualImages,
            new HashSet<Guid>(), context, []);

        Assert.False(receipt.Complete);
        var issueCodes = receipt.Issues.Select(x => x.Code).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("vision_owner_mismatch", issueCodes);
        Assert.Contains("vision_request_mismatch", issueCodes);
        Assert.Contains("vision_purpose_mismatch", issueCodes);
        Assert.Contains("vision_source_mismatch", issueCodes);
        Assert.Contains("vision_input_mismatch", issueCodes);
        Assert.Contains("vision_output_mismatch", issueCodes);
        Assert.Equal("Gap", Assert.Single(receipt.Batches).Status);
        Assert.Contains(receipt.Sources, x => x.Kind == NoteDerivedSourceKind.Photo &&
            x.Status == "Gap");
    }

    [Fact]
    public void MissingActualBatchRowsCannotBeReplacedByPlanMetadata()
    {
        var fixture = CreateCompleteFixture();

        var receipt = NoteDerivedReceipts.Build(fixture.State, [],
            new HashSet<Guid>(), fixture.Context, []);

        Assert.False(receipt.Complete);
        var batch = Assert.Single(receipt.Batches);
        Assert.Equal("Gap", batch.Status);
        Assert.Contains(batch.Issues, x => x.Code == "vision_batch_mismatch");
        Assert.Contains(receipt.Sources, x => x.Kind == NoteDerivedSourceKind.Photo &&
            x.Status == "Gap");
    }

    [Fact]
    public void AsrEvidenceBindsOwnerAttemptSegmentAndResultAndTamperIsGap()
    {
        var material = NoteDerivedFixture.CreateMaterial(withAudio: true);
        const string transcriptText = "synthetic ASR transcript";
        const string sourceSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var evidenceId = Guid.NewGuid();
        var transcript = new NoteDerivedTranscriptInput(0, 0, material.RecordingId, 1, 1, 0,
            $"recordings/{material.RecordingId:D}/s0001-c000001.wav", sourceSha, 0, 160,
            transcriptText, Guid.NewGuid(), evidenceId, "batch", "synthetic-provider");
        var snapshot = NoteDerivedFixture.Snapshot(material, [], [transcript]);
        var plan = new NoteDerivedPlanner().Build(snapshot);
        var taskId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var asrTaskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var state = new NoteDerivedTaskState
        {
            Owner = owner,
            Request = new(taskId, owner, material.VersionId, asrTaskId,
                new(Guid.NewGuid(), Guid.NewGuid(), "https://synthetic.invalid/v1/", true)),
            Fingerprint = "receipt-asr-test",
            NoteId = material.NoteId,
            InputSha256 = snapshot.InputSha256,
            ManifestSha256 = snapshot.ManifestSha256,
            Plan = plan,
            Coverage = plan.InitialCoverage,
            Issues = [],
            CompleteRecordArtifactId = Guid.NewGuid(),
            IndependentAnalysisArtifactId = Guid.NewGuid()
        };
        state.Steps = plan.Steps.Select(step => new NoteDerivedStepState
        {
            Key = step.Key,
            Kind = step.Kind,
            DependsOn = step.DependsOn,
            Images = step.Images,
            InputCharacters = step.InputCharacters,
            Attempts = [new NoteDerivedAttemptState { Status = "Queued" }]
        }).ToList();

        var asr = new NoteAsrState
        {
            Owner = owner,
            Request = new(asrTaskId, owner, material.VersionId,
                new(Guid.NewGuid(), Guid.NewGuid(), "https://synthetic.invalid/v1/chat/completions",
                    "mimo-asr", "1", true), "zh"),
            Fingerprint = "asr-receipt-test",
            NoteId = material.NoteId,
            InputSha256 = snapshot.InputSha256,
            ManifestSha256 = snapshot.ManifestSha256,
            AttemptId = attemptId,
            Steps =
            [
                new NoteAsrStep
                {
                    Index = 0,
                    RecordingId = transcript.RecordingId,
                    RecordingSegment = transcript.RecordingSegment,
                    ChunkIndex = transcript.ChunkIndex,
                    FileIndex = transcript.SourceFileIndex,
                    Path = transcript.SourcePath,
                    Sha256 = transcript.SourceSha256,
                    SourceOffset = transcript.SourceOffsetSamples,
                    Samples = transcript.Samples,
                    Attempts = [new NoteAsrAttempt { Status = "Succeeded", EvidenceId = evidenceId }]
                }
            ]
        };
        var proof = new NoteAsrSegmentEvidenceEntity
        {
            Id = evidenceId,
            OwnerId = owner,
            AttemptId = attemptId,
            TaskId = asrTaskId,
            VersionId = material.VersionId,
            Route = "local",
            FencingToken = 0,
            SegmentIndex = 0,
            RecordingId = transcript.RecordingId,
            RecordingSegment = transcript.RecordingSegment,
            ChunkIndex = transcript.ChunkIndex,
            SourceFileIndex = transcript.SourceFileIndex,
            SourceSha256 = transcript.SourceSha256,
            SourceOffsetSamples = transcript.SourceOffsetSamples,
            Samples = transcript.Samples,
            ReceiptKind = transcript.ReceiptKind!,
            ProviderHandle = transcript.ProviderHandle!,
            ResultText = transcript.Text,
            ResultSha256 = Hash(transcript.Text)
        };
        var context = new NoteDerivedReceiptContext(
            new Dictionary<Guid, AiRequestLogEntity>(),
            new Dictionary<Guid, IReadOnlyList<AiRequestLogEntity>>(),
            new Dictionary<string, NoteDerivedVersionDto>(),
            new Dictionary<Guid, AiCallAudioEntity>(),
            asr,
            new NoteAsrAttemptEntity
            {
                Id = attemptId, OwnerId = owner, TaskId = asrTaskId, NoteId = material.NoteId,
                VersionId = material.VersionId, InputSha256 = snapshot.InputSha256, Route = "local"
            },
            null,
            new Dictionary<int, NoteAsrSegmentEvidenceEntity> { [0] = proof });

        var valid = NoteDerivedReceipts.Build(state, [], new HashSet<Guid>(), context, []);
        var validSource = Assert.Single(valid.Sources.Where(x =>
            x.Kind == NoteDerivedSourceKind.TranscriptSegment));
        Assert.Equal("Consumed", validSource.Status);
        var binding = Assert.Single(validSource.Bindings);
        Assert.Equal("AsrEvidence", binding.Stage);
        Assert.Equal(evidenceId, binding.EvidenceId);
        Assert.True(binding.Verified);

        proof.ResultText = "tampered transcript";
        var tampered = NoteDerivedReceipts.Build(state, [], new HashSet<Guid>(), context, []);
        var tamperedSource = Assert.Single(tampered.Sources.Where(x =>
            x.Kind == NoteDerivedSourceKind.TranscriptSegment));
        Assert.Equal("Gap", tamperedSource.Status);
        Assert.Contains(tamperedSource.Issues, x => x.Code is "asr_evidence_mismatch" or "asr_output_mismatch");
    }

    [Fact]
    public void CloudAsrReceiptUsesTheActualProductionFingerprintShape()
    {
        var material = NoteDerivedFixture.CreateMaterial(withAudio: true);
        var owner = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var asrTaskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var logId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var modelRevision = Guid.NewGuid();
        const string endpoint = "https://synthetic.invalid/v1/chat/completions";
        const string presetId = "mimo-asr";
        const string presetVersion = "1";
        const string transcriptText = "synthetic cloud ASR transcript";
        const string sourceSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string promptText = "synthetic ASR prompt";
        var promptSha = Hash(promptText);
        var promptRevision = Hash(promptText + ":revision");
        var promptTemplateSha = Hash("synthetic ASR template");
        var promptTemplateRevision = Hash("synthetic ASR template:revision");
        var audioSha = Hash("synthetic segment wave bytes");
        var audio = new AiCallAudioEntity
        {
            Id = Guid.NewGuid(),
            LogId = logId,
            Name = "segment-00001.wav",
            Format = "wav",
            SizeBytes = 44 + 160 * 2,
            Sha256 = audioSha,
            ObjectKey = "synthetic/cloud-asr/segment-00001.wav",
            ProviderKey = "synthetic"
        };
        var consent = new AiAsrDestinationConsent(modelId, modelRevision, endpoint,
            presetId, presetVersion, true);
        var inputHash = AsrFingerprint(consent, "zh", promptRevision, promptSha, audio);
        var log = Log(owner, taskId, requestId, "ai.asr-call", modelId, modelRevision,
            endpoint, inputHash, transcriptText);
        log.Id = logId;
        log.SourceObjectType = "native-note-asr-task";
        log.SourceObjectId = asrTaskId.ToString("D");
        var transcript = new NoteDerivedTranscriptInput(0, 0, material.RecordingId, 1, 1, 5,
            $"recordings/{material.RecordingId:D}/s0001-c000001.wav", sourceSha, 0, 160,
            transcriptText, logId);
        var snapshot = NoteDerivedFixture.Snapshot(material, [], [transcript]);
        var plan = new NoteDerivedPlanner().Build(snapshot);
        var state = new NoteDerivedTaskState
        {
            Owner = owner,
            Request = new(taskId, owner, material.VersionId, asrTaskId,
                new NoteDerivedGenerationConsent(modelId, modelRevision, endpoint, true)),
            Fingerprint = "cloud-asr-receipt-test",
            NoteId = material.NoteId,
            InputSha256 = snapshot.InputSha256,
            ManifestSha256 = snapshot.ManifestSha256,
            Plan = plan,
            Coverage = plan.InitialCoverage,
            Issues = [],
            CompleteRecordArtifactId = Guid.NewGuid(),
            IndependentAnalysisArtifactId = Guid.NewGuid()
        };
        state.Steps = plan.Steps.Select(step => new NoteDerivedStepState
        {
            Key = step.Key,
            Kind = step.Kind,
            DependsOn = step.DependsOn,
            Images = step.Images,
            InputCharacters = step.InputCharacters,
            Attempts = [new NoteDerivedAttemptState { Status = "Queued" }]
        }).ToList();
        var asr = new NoteAsrState
        {
            Owner = owner,
            Request = new(asrTaskId, owner, material.VersionId, consent, "zh"),
            Fingerprint = "cloud-asr-test",
            NoteId = material.NoteId,
            InputSha256 = snapshot.InputSha256,
            ManifestSha256 = snapshot.ManifestSha256,
            AttemptId = attemptId,
            Steps =
            [
                new NoteAsrStep
                {
                    Index = 0,
                    RecordingId = transcript.RecordingId,
                    RecordingSegment = transcript.RecordingSegment,
                    ChunkIndex = transcript.ChunkIndex,
                    FileIndex = transcript.SourceFileIndex,
                    Path = transcript.SourcePath,
                    Sha256 = transcript.SourceSha256,
                    SourceOffset = transcript.SourceOffsetSamples,
                    Samples = transcript.Samples,
                    Attempts =
                    [
                        new NoteAsrAttempt
                        {
                            RequestId = requestId,
                            LogId = logId,
                            Status = "Succeeded"
                        }
                    ]
                }
            ]
        };
        var attempt = new NoteAsrAttemptEntity
        {
            Id = attemptId,
            OwnerId = owner,
            TaskId = asrTaskId,
            NoteId = material.NoteId,
            VersionId = material.VersionId,
            InputSha256 = snapshot.InputSha256,
            Route = NoteAsrRoute.CloudMultimodal,
            ModelId = modelId,
            ModelRevision = modelRevision,
            PresetId = presetId,
            PresetVersion = presetVersion,
            Endpoint = endpoint,
            PromptRendered = promptText,
            PromptSha256 = promptSha,
            PromptRevision = promptRevision,
            PromptTemplateSha256 = promptTemplateSha,
            PromptTemplateRevision = promptTemplateRevision,
            Language = "zh"
        };
        var context = new NoteDerivedReceiptContext(
            new Dictionary<Guid, AiRequestLogEntity> { [log.Id] = log },
            new Dictionary<Guid, IReadOnlyList<AiRequestLogEntity>> { [requestId] = [log] },
            new Dictionary<string, NoteDerivedVersionDto>(),
            new Dictionary<Guid, AiCallAudioEntity> { [log.Id] = audio },
            asr,
            attempt,
            null,
            new Dictionary<int, NoteAsrSegmentEvidenceEntity>());

        var receipt = NoteDerivedReceipts.Build(state, [], new HashSet<Guid>(), context, []);
        var source = Assert.Single(receipt.Sources.Where(x =>
            x.Kind == NoteDerivedSourceKind.TranscriptSegment));
        var binding = Assert.Single(source.Bindings.Where(x => x.Stage == "AsrReceipt"));
        Assert.True(binding.Verified);
        Assert.Equal(inputHash, binding.InputSha256);
        Assert.DoesNotContain(source.Issues, x => x.Code.StartsWith("asr_", StringComparison.Ordinal));

        log.InputHash = Hash("wrong ASR fingerprint shape");
        var tampered = NoteDerivedReceipts.Build(state, [], new HashSet<Guid>(), context, []);
        var tamperedSource = Assert.Single(tampered.Sources.Where(x =>
            x.Kind == NoteDerivedSourceKind.TranscriptSegment));
        Assert.Contains(tamperedSource.Issues, x => x.Code == "asr_input_mismatch");
        Assert.Contains(tamperedSource.Bindings, x => x.Stage == "AsrReceipt" && !x.Verified);
    }

    [Fact]
    public void SynthesisReceiptValidationRejectsTamperedIdentityContentAndHashes()
    {
        var fixture = CreateCompleteFixture();
        var step = fixture.State.Steps.Single(x => x.Key == NoteDerivedPrompts.RecordKey);
        var log = fixture.Context.LogsById.Values.Single(x =>
            x.Purpose == NoteDerivedPrompts.RecordPurpose);
        var text = log.ResponseText!;

        Assert.Empty(NoteDerivedReceipts.ValidateSynthesisReceipt(
            fixture.State, step, log, text, log.InputHash));

        var originalUser = log.UserId;
        log.UserId = Guid.NewGuid();
        Assert.Contains(NoteDerivedReceipts.ValidateSynthesisReceipt(
                fixture.State, step, log, text, log.InputHash),
            issue => issue.Code == "synthesis_owner_mismatch");
        log.UserId = originalUser;

        var originalRequest = log.ClientRequestId;
        log.ClientRequestId = Guid.NewGuid();
        Assert.Contains(NoteDerivedReceipts.ValidateSynthesisReceipt(
                fixture.State, step, log, text, log.InputHash),
            issue => issue.Code == "synthesis_request_mismatch");
        log.ClientRequestId = originalRequest;

        var originalPurpose = log.Purpose;
        log.Purpose = "note.derived.tampered";
        Assert.Contains(NoteDerivedReceipts.ValidateSynthesisReceipt(
                fixture.State, step, log, text, log.InputHash),
            issue => issue.Code == "synthesis_purpose_mismatch");
        log.Purpose = originalPurpose;

        var originalSource = log.SourceObjectId;
        log.SourceObjectId = Guid.NewGuid().ToString("D");
        Assert.Contains(NoteDerivedReceipts.ValidateSynthesisReceipt(
                fixture.State, step, log, text, log.InputHash),
            issue => issue.Code == "synthesis_source_mismatch");
        log.SourceObjectId = originalSource;

        var originalModel = log.ModelProfileId;
        log.ModelProfileId = Guid.NewGuid();
        Assert.Contains(NoteDerivedReceipts.ValidateSynthesisReceipt(
                fixture.State, step, log, text, log.InputHash),
            issue => issue.Code == "synthesis_model_mismatch");
        log.ModelProfileId = originalModel;

        var originalInput = log.InputHash;
        log.InputHash = Hash("tampered input");
        Assert.Contains(NoteDerivedReceipts.ValidateSynthesisReceipt(
                fixture.State, step, log, text, originalInput),
            issue => issue.Code == "synthesis_input_mismatch");
        log.InputHash = originalInput;

        var originalOutput = log.OutputHash;
        log.OutputHash = Hash("tampered output");
        Assert.Contains(NoteDerivedReceipts.ValidateSynthesisReceipt(
                fixture.State, step, log, text, log.InputHash),
            issue => issue.Code == "synthesis_output_mismatch");
        log.OutputHash = originalOutput;

        log.ResponseText = "tampered synthesis text";
        log.OutputHash = Hash(log.ResponseText);
        Assert.Contains(NoteDerivedReceipts.ValidateSynthesisReceipt(
                fixture.State, step, log, text, log.InputHash),
            issue => issue.Code == "synthesis_result_mismatch");
        log.ResponseText = text;
        log.OutputHash = originalOutput;

        var originalLogId = step.Current.LogId;
        step.Current.LogId = Guid.NewGuid();
        Assert.Contains(NoteDerivedReceipts.ValidateSynthesisReceipt(
                fixture.State, step, log, text, log.InputHash),
            issue => issue.Code == "synthesis_log_id_mismatch");
        step.Current.LogId = originalLogId;

        Assert.Contains(NoteDerivedReceipts.ValidateSynthesisReceipt(
                fixture.State, step, log, text, null),
            issue => issue.Code == "synthesis_input_missing");
    }

    [Fact]
    public void LegacySynthesisAudioWireFailsClosedOnInvalidRootFlagsAndSnapshot()
    {
        var (state, step) = CreateLegacyAudioState();
        var audio = Assert.Single(state.Plan.Input.Audio);
        const string messages = """
            [{"role":"user","content":[
              {"type":"input_audio","input_audio":{"data":"pim-ai-audio:00001","format":"wav"}}]}]
            """;
        var validMetadata = $$"""
            {"providerAttempted":true,"requestBodyCaptured":true,"responseBodyComplete":true,
             "audioEncoding":"sha256-reference","audioInputsReferenced":1,
             "audioInputsSha256":["{{audio.SourceSha256}}"],
             "audioContextOwnerId":"{{state.Owner:D}}",
             "audioContextNoteId":"{{state.NoteId:D}}",
             "audioContextSourceVersionId":"{{state.Request.VersionId:D}}"}
            """;
        var log = new AiRequestLogEntity
        {
            MetadataJson = validMetadata,
            RequestMessagesJson = messages
        };
        Assert.Empty(NoteDerivedReceipts.ValidateSynthesisAudioWire(state, step, log));

        log.MetadataJson = "[]";
        Assert.Contains(NoteDerivedReceipts.ValidateSynthesisAudioWire(state, step, log),
            issue => issue.Code == "audio_wire_incomplete");

        log.MetadataJson = """
            {"providerAttempted":true,"requestBodyCaptured":true,"responseBodyComplete":false,
             "audioEncoding":"sha256-reference","audioInputsReferenced":0}
            """;
        Assert.Contains(NoteDerivedReceipts.ValidateSynthesisAudioWire(state, step, log),
            issue => issue.Code == "audio_wire_incomplete");

        log.MetadataJson = validMetadata.Replace(audio.SourceSha256, "not-a-digest");
        Assert.Contains(NoteDerivedReceipts.ValidateSynthesisAudioWire(state, step, log),
            issue => issue.Code == "audio_wire_incomplete");

        log.MetadataJson = validMetadata;
        log.RequestMessagesJson = "[]";
        Assert.Contains(NoteDerivedReceipts.ValidateSynthesisAudioWire(state, step, log),
            issue => issue.Code == "audio_wire_mismatch");
    }

    private static (NoteDerivedTaskState State, NoteDerivedStepState Step) CreateLegacyAudioState()
    {
        const int samples = 160;
        var material = NoteDerivedFixture.CreateMaterial(withAudio: true, audioSamples: samples);
        var audio = new NoteDerivedAudioInput(0, material.RecordingId, 1, 1, 5,
            $"recordings/{material.RecordingId:D}/s0001-c000001.wav", Hash("synthetic audio bytes"),
            0, samples, 44 + samples * 2, "segment-00001.wav");
        var snapshot = NoteDerivedFixture.Snapshot(material) with { Audio = [audio] };
        var plan = new NoteDerivedPlanner().Build(snapshot);
        var owner = Guid.NewGuid();
        var state = new NoteDerivedTaskState
        {
            Owner = owner,
            Request = new(Guid.NewGuid(), owner, material.VersionId, null,
                new(Guid.NewGuid(), Guid.NewGuid(), "https://synthetic.invalid/v1/", true)),
            Fingerprint = "legacy-audio-wire-test",
            NoteId = material.NoteId,
            InputSha256 = snapshot.InputSha256,
            ManifestSha256 = snapshot.ManifestSha256,
            Plan = plan,
            Coverage = plan.InitialCoverage,
            Issues = [],
            CompleteRecordArtifactId = Guid.NewGuid(),
            IndependentAnalysisArtifactId = Guid.NewGuid()
        };
        state.Steps = plan.Steps.Select(step => new NoteDerivedStepState
        {
            Key = step.Key,
            Kind = step.Kind,
            DependsOn = step.DependsOn,
            Images = step.Images,
            InputCharacters = step.InputCharacters,
            Audio = step.Audio ?? [],
            TranscriptOrdinals = step.TranscriptOrdinals ?? [],
            MaxOutputCharacters = step.MaxOutputCharacters,
            Attempts = [new NoteDerivedAttemptState { Status = "Queued" }]
        }).ToList();
        return (state, state.Steps.Single(x => x.Key == NoteDerivedPrompts.RecordKey));
    }

    private static Fixture CreateCompleteFixture()
    {
        var material = NoteDerivedFixture.CreateMaterial(withAudio: false);
        var images = new[]
        {
            NoteDerivedFixture.Image(0, material.PhotoIndex, "Photo",
                NoteDerivedFixture.Digest(NoteDerivedFixture.PhotoBytes),
                NoteDerivedFixture.PhotoBytes.Length),
            NoteDerivedFixture.Image(1, material.InkIndex, "InkPreview",
                NoteDerivedFixture.Digest(NoteDerivedFixture.InkBytes),
                NoteDerivedFixture.InkBytes.Length)
        };
        var snapshot = NoteDerivedFixture.Snapshot(material, images);
        var plan = new NoteDerivedPlanner().Build(snapshot);
        var taskId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var modelRevision = Guid.NewGuid();
        const string endpoint = "https://synthetic.invalid/v1/";
        var consent = new NoteDerivedGenerationConsent(modelId, modelRevision, endpoint, true);
        var state = new NoteDerivedTaskState
        {
            Owner = owner,
            Request = new(taskId, owner, material.VersionId, null, consent),
            Fingerprint = "receipt-test",
            NoteId = material.NoteId,
            InputSha256 = snapshot.InputSha256,
            ManifestSha256 = snapshot.ManifestSha256,
            Plan = plan,
            Coverage = plan.InitialCoverage,
            Issues = [],
            CompleteRecordArtifactId = Guid.NewGuid(),
            IndependentAnalysisArtifactId = Guid.NewGuid()
        };
        state.Steps = plan.Steps.Select(step => new NoteDerivedStepState
        {
            Key = step.Key,
            Kind = step.Kind,
            DependsOn = step.DependsOn,
            Images = step.Images,
            InputCharacters = step.InputCharacters,
            Attempts = [new NoteDerivedAttemptState { Status = "Queued" }]
        }).ToList();

        var visionStep = state.Steps.Single(x => x.Key == "image-batch:0001");
        var recordStep = state.Steps.Single(x => x.Key == NoteDerivedPrompts.RecordKey);
        var analysisStep = state.Steps.Single(x => x.Key == NoteDerivedPrompts.AnalysisKey);
        const string visionResponse = "synthetic visual evidence";
        const string recordResponse = "synthetic complete record";
        const string analysisResponse = "synthetic independent analysis";
        var visionLog = Log(owner, taskId, visionStep.Current.RequestId,
            NoteDerivedPrompts.VisionPurpose, modelId, modelRevision, endpoint,
            VisionFingerprint(state, visionStep, NoteDerivedPrompts.ImageEvidence(
                visionStep.Key, visionStep.Images)), visionResponse);
        visionLog.MetadataJson = JsonSerializer.Serialize(new
        {
            providerAttempted = true,
            requestBodyCaptured = true,
            responseBodyComplete = true,
            imageInputsStored = true
        }, Json);
        visionStep.Current.Status = "Succeeded";
        visionStep.Current.LogId = visionLog.Id;
        visionStep.Current.ResultSha256 = Hash(visionResponse);

        var evidence = new[]
        {
            new NoteDerivedEvidence(visionStep.Key, visionLog.Id, visionResponse, images)
        };
        var recordPrompt = NoteDerivedPrompts.Record(snapshot, evidence);
        var recordLog = Log(owner, taskId, recordStep.Current.RequestId,
            NoteDerivedPrompts.RecordPurpose, modelId, modelRevision, endpoint,
            TextFingerprint(state, NoteDerivedPrompts.RecordPurpose, recordPrompt), recordResponse);
        recordStep.Current.Status = "Succeeded";
        recordStep.Current.LogId = recordLog.Id;
        recordStep.Current.ResultSha256 = Hash(recordResponse);
        var recordVersionId = Guid.NewGuid();
        var recordVersion = Version(state, recordVersionId, NoteDerivedArtifactKind.CompleteRecord,
            recordLog, recordResponse);

        var analysisPrompt = NoteDerivedPrompts.Analysis(snapshot, recordVersionId,
            recordResponse, evidence);
        var analysisLog = Log(owner, taskId, analysisStep.Current.RequestId,
            NoteDerivedPrompts.AnalysisPurpose, modelId, modelRevision, endpoint,
            TextFingerprint(state, NoteDerivedPrompts.AnalysisPurpose, analysisPrompt),
            analysisResponse);
        analysisStep.Current.Status = "Succeeded";
        analysisStep.Current.LogId = analysisLog.Id;
        analysisStep.Current.ResultSha256 = Hash(analysisResponse);
        var analysisVersion = Version(state, Guid.NewGuid(),
            NoteDerivedArtifactKind.IndependentAnalysis, analysisLog, analysisResponse,
            recordVersionId);

        var logsById = new Dictionary<Guid, AiRequestLogEntity>
        {
            [visionLog.Id] = visionLog,
            [recordLog.Id] = recordLog,
            [analysisLog.Id] = analysisLog
        };
        var logsByRequest = new Dictionary<Guid, IReadOnlyList<AiRequestLogEntity>>
        {
            [visionStep.Current.RequestId] = [visionLog],
            [recordStep.Current.RequestId] = [recordLog],
            [analysisStep.Current.RequestId] = [analysisLog]
        };
        var versions = new Dictionary<string, NoteDerivedVersionDto>(StringComparer.Ordinal)
        {
            [recordStep.Key] = recordVersion,
            [analysisStep.Key] = analysisVersion
        };
        var actual = new[]
        {
            new NoteDerivedActualImage(visionLog.Id, 0, NoteDerivedPrompts.ImageName(images[0]),
                images[0].ContentType, images[0].SizeBytes, images[0].Sha256, true),
            new NoteDerivedActualImage(visionLog.Id, 1, NoteDerivedPrompts.ImageName(images[1]),
                images[1].ContentType, images[1].SizeBytes, images[1].Sha256, true)
        };
        return new(state, actual, visionLog,
            new NoteDerivedReceiptContext(logsById, logsByRequest, versions,
                new Dictionary<Guid, AiCallAudioEntity>(), null, null, null,
                new Dictionary<int, NoteAsrSegmentEvidenceEntity>()),
            material.Markdown, visionResponse);
    }

    private static NoteDerivedVersionDto Version(NoteDerivedTaskState state, Guid versionId,
        NoteDerivedArtifactKind kind, AiRequestLogEntity log, string content,
        Guid? sourceRecordVersionId = null) =>
        new(versionId, Guid.NewGuid(), kind, NoteDerivedVersionAuthor.Model,
            null, null, sourceRecordVersionId, state.Request.VersionId,
            state.Request.TaskId, content, Hash(content), state.InputSha256,
            state.ManifestSha256, state.Plan.PlanVersion, NoteDerivedPrompts.PromptVersion,
            log.Id, state.Request.Consent.ModelId, state.Request.Consent.ModelRevision,
            state.Request.Consent.Endpoint, null, false, DateTimeOffset.UtcNow, true);

    private static AiRequestLogEntity Log(Guid owner, Guid taskId, Guid requestId,
        string purpose, Guid modelId, Guid modelRevision, string endpoint,
        string inputHash, string response) => new()
    {
        Id = Guid.NewGuid(),
        ClientRequestId = requestId,
        UserId = owner,
        Purpose = purpose,
        SourceObjectType = NoteDerivedPrompts.SourceObjectType,
        SourceObjectId = taskId.ToString("D"),
        Status = "succeeded",
        ModelProfileId = modelId,
        ModelRevision = modelRevision,
        ModelEndpoint = endpoint,
        InputHash = inputHash,
        OutputHash = Hash(response),
        ResponseText = response
    };

    private static string VisionFingerprint(NoteDerivedTaskState state,
        NoteDerivedStepState step, string prompt)
    {
        var consent = new AiVisionDestinationConsent(state.Request.Consent.ModelId,
            state.Request.Consent.ModelRevision, state.Request.Consent.Endpoint, true);
        return Hash(JsonSerializer.Serialize(new
        {
            kind = "vision",
            sourceObjectType = NoteDerivedPrompts.SourceObjectType,
            sourceObjectId = state.Request.TaskId.ToString("D"),
            purpose = NoteDerivedPrompts.VisionPurpose,
            ModelId = (Guid?)state.Request.Consent.ModelId,
            SystemPrompt = (string?)null,
            Prompt = prompt,
            consent,
            images = step.Images.OrderBy(x => x.Ordinal).Select(x => new
            {
                Name = NoteDerivedPrompts.ImageName(x),
                x.ContentType,
                x.Sha256,
                size = x.SizeBytes
            })
        }, Json));
    }

    private static string TextFingerprint(NoteDerivedTaskState state, string purpose, string prompt)
    {
        var consent = new AiVisionDestinationConsent(state.Request.Consent.ModelId,
            state.Request.Consent.ModelRevision, state.Request.Consent.Endpoint, true);
        return Hash(JsonSerializer.Serialize(new
        {
            kind = "text",
            sourceObjectType = NoteDerivedPrompts.SourceObjectType,
            sourceObjectId = state.Request.TaskId.ToString("D"),
            purpose,
            ModelId = (Guid?)state.Request.Consent.ModelId,
            SystemPrompt = (string?)null,
            Prompt = prompt,
            consent
        }, Json));
    }

    private static string AsrFingerprint(AiAsrDestinationConsent consent, string language,
        string promptRevision, string promptSha256, AiCallAudioEntity audio) =>
        Hash(JsonSerializer.Serialize(new
        {
            kind = "asr",
            consent.ModelId,
            language,
            consent,
            Revision = promptRevision,
            promptSha256,
            audio.Name,
            audio.Format,
            audio.Sha256,
            size = audio.SizeBytes
        }, Json));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record Fixture(
        NoteDerivedTaskState State,
        IReadOnlyList<NoteDerivedActualImage> ActualImages,
        AiRequestLogEntity VisionLog,
        NoteDerivedReceiptContext Context,
        string Markdown,
        string VisionResponse);
}

public sealed class NoteDerivedNoAudioWireTests
{
    [Fact]
    public void NoAudioWireAcceptsExplicitZeroWithNoEncoding()
    {
        var issues = Check("""
            {"providerAttempted":true,"requestBodyCaptured":true,"responseBodyComplete":true,
             "audioInputsReferenced":0,"audioInputsSha256":[],"audioEncoding":null}
            """);

        Assert.Empty(issues);
    }

    [Fact]
    public void NoAudioWireAcceptsMissingHashesAndEncodingButRequiresExplicitZero()
    {
        var issues = Check("""
            {"providerAttempted":true,"requestBodyCaptured":true,"responseBodyComplete":true,
             "audioInputsReferenced":0}
            """);

        Assert.Empty(issues);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    [InlineData("""{"providerAttempted":false,"requestBodyCaptured":true,"responseBodyComplete":true,"audioInputsReferenced":0}""")]
    [InlineData("""{"providerAttempted":true,"requestBodyCaptured":false,"responseBodyComplete":true,"audioInputsReferenced":0}""")]
    [InlineData("""{"providerAttempted":true,"requestBodyCaptured":true,"responseBodyComplete":false,"audioInputsReferenced":0}""")]
    [InlineData("""{"providerAttempted":true,"requestBodyCaptured":true,"responseBodyComplete":true}""")]
    [InlineData("""{"providerAttempted":true,"requestBodyCaptured":true,"responseBodyComplete":true,"audioInputsReferenced":null}""")]
    [InlineData("""{"providerAttempted":true,"requestBodyCaptured":true,"responseBodyComplete":true,"audioInputsReferenced":"0"}""")]
    [InlineData("""{"providerAttempted":true,"requestBodyCaptured":true,"responseBodyComplete":true,"audioInputsReferenced":1}""")]
    [InlineData("""{"providerAttempted":true,"requestBodyCaptured":true,"responseBodyComplete":true,"audioInputsReferenced":0,"audioEncoding":"sha256-reference"}""")]
    [InlineData("""{"providerAttempted":true,"requestBodyCaptured":true,"responseBodyComplete":true,"audioInputsReferenced":0,"audioEncoding":""}""")]
    [InlineData("""{"providerAttempted":true,"requestBodyCaptured":true,"responseBodyComplete":true,"audioInputsReferenced":0,"audioInputsSha256":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"]}""")]
    [InlineData("""{"providerAttempted":true,"requestBodyCaptured":true,"responseBodyComplete":true,"audioInputsReferenced":0,"audioInputsSha256":[123]}""")]
    [InlineData("""{"providerAttempted":true,"requestBodyCaptured":true,"responseBodyComplete":true,"audioInputsReferenced":0,"audioInputsSha256":null}""")]
    [InlineData("""{"providerAttempted":true,"requestBodyCaptured":true,"responseBodyComplete":true,"audioInputsReferenced":0,"audioInputsSha256":"aa"}""")]
    public void NoAudioWireFailsClosedOnIncompleteOrAmbiguousMetadata(string metadata)
    {
        var issues = Check(metadata);

        Assert.NotEmpty(issues);
        Assert.Contains(issues, issue => issue.Code is
            "audio_replayed_in_synthesis" or "audio_replay_unverifiable");
    }

    [Fact]
    public void NoAudioWireRejectsRealAudioPartsButNotReferenceTextInThePrompt()
    {
        var promptOnly = NoteDerivedReceipts.ValidateNoAudioWire(Log("""
            {"providerAttempted":true,"requestBodyCaptured":true,"responseBodyComplete":true,
             "audioInputsReferenced":0,"audioInputsSha256":[],"audioEncoding":null}
            """, """
            [{"role":"user","content":"prompt text may mention pim-ai-audio:00001, input_audio or data:audio/wav;base64,AAAA without an audio part"}]
            """), "record-synthesis");
        Assert.Empty(promptOnly);

        var issues = NoteDerivedReceipts.ValidateNoAudioWire(Log("""
            {"providerAttempted":true,"requestBodyCaptured":true,"responseBodyComplete":true,
             "audioInputsReferenced":0,"audioInputsSha256":[],"audioEncoding":null}
            """, """[{"role":"user","content":[{"type":"input_audio"}]}]"""),
            "record-synthesis");
        Assert.Contains(issues, issue => issue.Code == "audio_replayed_in_synthesis");
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("""[{"role":"user","content":[42]}]""")]
    [InlineData("""[{"role":"user","content":[{"input_audio":{"data":"x"}}]}]""")]
    [InlineData("""[{"role":"user","content":[{"type":"input_audio","input_audio":{}}]}]""")]
    public void NoAudioWireFailsClosedOnUnparseableOrAudioBearingSnapshots(string messages)
    {
        var issues = NoteDerivedReceipts.ValidateNoAudioWire(Log("""
            {"providerAttempted":true,"requestBodyCaptured":true,"responseBodyComplete":true,
             "audioInputsReferenced":0,"audioInputsSha256":[],"audioEncoding":null}
            """, messages), "record-synthesis");

        Assert.Contains(issues, issue => issue.Code == "audio_replayed_in_synthesis");
    }

    private static IReadOnlyList<NoteDerivedIssueDto> Check(string metadata) =>
        NoteDerivedReceipts.ValidateNoAudioWire(Log(metadata), "record-synthesis");

    private static AiRequestLogEntity Log(string metadata, string messages = "[]") => new()
    {
        MetadataJson = metadata,
        RequestMessagesJson = messages
    };
}

public sealed class NoteDerivedAudioRequestSnapshotTests
{
    [Fact]
    public void ExactAudioPartsAreAcceptedEvenWhenThePromptTextContainsAudioLikeStrings()
    {
        const string messages = """
            [{"role":"user","content":[
              {"type":"text","text":"The prompt may mention pim-ai-audio:99999 or input_audio without sending audio."},
              {"type":"input_audio","input_audio":{"data":"pim-ai-audio:00001","format":"wav"}},
              {"type":"input_audio","input_audio":{"data":"pim-ai-audio:00002","format":"wav"}}]}]
            """;

        Assert.True(NoteDerivedReceipts.AudioRequestSnapshotMatchesExactly(messages, 2));
    }

    [Theory]
    [InlineData("""[{"role":"user","content":[{"type":"input_audio","input_audio":{"data":"pim-ai-audio:00001","format":"wav"}}]}]""", 2)]
    [InlineData("""[{"role":"user","content":[{"type":"input_audio","input_audio":{"data":"pim-ai-audio:00001","format":"wav"}},{"type":"input_audio","input_audio":{"data":"pim-ai-audio:00002","format":"wav"}},{"type":"input_audio","input_audio":{"data":"pim-ai-audio:00003","format":"wav"}}]}]""", 2)]
    [InlineData("""[{"role":"user","content":[{"type":"input_audio","input_audio":{"data":"pim-ai-audio:00002","format":"wav"}}]}]""", 1)]
    [InlineData("""[{"role":"user","content":[{"type":"input_audio","input_audio":{"data":"pim-ai-audio:00001","format":"wav"}},{"type":"input_audio","input_audio":{"data":"pim-ai-audio:00001","format":"wav"}}]}]""", 2)]
    [InlineData("""[{"role":"user","content":[{"type":"input_audio","input_audio":{"data":123,"format":"wav"}}]}]""", 1)]
    [InlineData("""[{"role":"user","content":[{"type":"input_audio","input_audio":{"data":"pim-ai-audio:00001","format":"mp3"}}]}]""", 1)]
    [InlineData("""[{"role":"user","content":[{"input_audio":{"data":"pim-ai-audio:00001","format":"wav"}}]}]""", 1)]
    [InlineData("""[{"role":"user","content":[{"type":"input_audio","input_audio":{}}]}]""", 1)]
    [InlineData("""[{"role":"user","content":"pim-ai-audio:00001"}]""", 1)]
    [InlineData("""[{"role":"user","content":[42]}]""", 1)]
    [InlineData("not-json", 1)]
    public void NonExactAudioPartsAreRejected(string messages, int expectedCount)
    {
        Assert.False(NoteDerivedReceipts.AudioRequestSnapshotMatchesExactly(
            messages, expectedCount));
    }
}
