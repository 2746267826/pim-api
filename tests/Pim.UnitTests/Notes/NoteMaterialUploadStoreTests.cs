using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Notes;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteMaterialUploadStoreTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pim-note-upload-tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _user = Guid.NewGuid();
    private readonly NoteMaterialUploadOptions _options;

    public NoteMaterialUploadStoreTests() => _options = new()
    {
        StorageRoot = _root, MaxVersionBytes = 32 * 1024 * 1024, MaxUserReservedBytes = 64 * 1024 * 1024
    };

    [Fact]
    public async Task Batch_jsonl_uses_and_validates_the_profile_output_token_limit()
    {
        var fixture = Fixture.Create();
        var store = Store();
        await store.BeginAsync(_user, fixture.Request);
        await UploadAll(store, fixture);
        await store.CompleteAsync(_user, fixture.Request.VersionId);
        var version = await store.GetAsync(_user, fixture.Request.VersionId);
        var steps = NoteAsrPlan.Build(version, NoteAsrPlan.SamplesPerCall);
        var ids = new[] { NoteAsrBatchJsonlReader.CustomId(0) };
        const string prompt = "逐字转写，保留时间轴。";
        const int maxBytes = 4 * 1024 * 1024;

        await using (var generated = await NoteAsrBatchJsonlReader.GenerateFromPlanAsync(
            _user, fixture.Request.VersionId, steps, NoteAsrPlan.SamplesPerCall, "zh",
            "mimo-v2.6-flash", prompt, store, maxBytes, ids, [0], 4096,
            CancellationToken.None))
        {
            using var reader = new StreamReader(generated.Content, Encoding.UTF8, leaveOpen: true);
            using var line = JsonDocument.Parse(await reader.ReadLineAsync() ?? "{}");
            Assert.Equal(4096, line.RootElement.GetProperty("body")
                .GetProperty("max_completion_tokens").GetInt32());
        }

        await using (var generated = await NoteAsrBatchJsonlReader.GenerateFromPlanAsync(
            _user, fixture.Request.VersionId, steps, NoteAsrPlan.SamplesPerCall, "zh",
            "mimo-v2.6-flash", prompt, store, maxBytes, ids, [0], 4096,
            CancellationToken.None))
        {
            var wrong = await Assert.ThrowsAsync<DomainException>(() =>
                NoteAsrBatchJsonlReader.ReadAsync(generated.Content, generated.Length,
                    _user, fixture.Request.VersionId, steps, NoteAsrPlan.SamplesPerCall, "zh",
                    "mimo-v2.6-flash", prompt, store, maxBytes, ids, 8192,
                    CancellationToken.None));
            Assert.Equal(40001, wrong.ErrorCode);
        }

        await using (var generated = await NoteAsrBatchJsonlReader.GenerateFromPlanAsync(
            _user, fixture.Request.VersionId, steps, NoteAsrPlan.SamplesPerCall, "zh",
            "mimo-v2.6-flash", prompt, store, maxBytes, ids, [0], 4096,
            CancellationToken.None))
        {
            await using var parsed = await NoteAsrBatchJsonlReader.ReadAsync(
                generated.Content, generated.Length, _user, fixture.Request.VersionId, steps,
                NoteAsrPlan.SamplesPerCall, "zh", "mimo-v2.6-flash", prompt, store,
                maxBytes, ids, 4096, CancellationToken.None);
            Assert.Equal(ids, parsed.CustomIds);
        }
    }

    [Fact]
    public async Task MixedVersionResumesAfterReopeningAndRoundTripsAudioLargerThanFourMb()
    {
        var fixture = Fixture.Create(audioSamples: 16_000 * 180);
        var store = Store();
        var begun = await store.BeginAsync(_user, fixture.Request);
        Assert.Equal("Uploading", begun.Phase);
        Assert.Null(begun.CurrentVersionId);
        Assert.True(fixture.Content.Last().Length > 4 * 1024 * 1024);
        await UploadPart(store, fixture, 0, 0);
        var reopened = Store();
        var resumed = await reopened.BeginAsync(_user, fixture.Request);
        Assert.Equal(new[] { 0 }, resumed.Files[0].UploadedParts);
        await UploadAll(reopened, fixture);
        var accepted = await reopened.CompleteAsync(_user, fixture.Request.VersionId);
        Assert.Equal("Uploaded", accepted.Phase);
        Assert.True(accepted.IsCurrent);
        Assert.False(accepted.HasConflict);
        Assert.Equal(JsonSerializer.Serialize(accepted, Json), JsonSerializer.Serialize(await reopened.CompleteAsync(_user, fixture.Request.VersionId), Json));
        var read = await Store().GetAsync(_user, fixture.Request.VersionId);
        Assert.Equal(fixture.Request.InputJson, read.InputJson);
        Assert.Equal(fixture.Request.ManifestJson, read.ManifestJson);
        for (var i = 0; i < fixture.Content.Count; i++)
        {
            using var output = new MemoryStream();
            await Store().ReadFileAsync(_user, fixture.Request.VersionId, i, output);
            Assert.Equal(fixture.Content[i], output.ToArray());
        }
    }

    [Fact]
    public async Task MissingOrCorruptedMaterialCannotPublishOrReplaceTheCurrentVersion()
    {
        var fixture = Fixture.Create(); var store = Store();
        await store.BeginAsync(_user, fixture.Request);
        await Error(40901, () => store.CompleteAsync(_user, fixture.Request.VersionId));
        Assert.Null((await store.GetAsync(_user, fixture.Request.VersionId)).Upload.CurrentVersionId);
        await UploadAll(store, fixture);
        var path = Path.Combine(_root, "binary", _user.ToString("D"), "note-parts", fixture.Request.VersionId.ToString("D"), Hash(Encoding.UTF8.GetBytes("0/0")) + ".blob");
        var bytes = await File.ReadAllBytesAsync(path); bytes[0] ^= 1; await File.WriteAllBytesAsync(path, bytes);
        await Error(40001, () => store.CompleteAsync(_user, fixture.Request.VersionId));
        Assert.Equal("Uploading", (await store.GetAsync(_user, fixture.Request.VersionId)).Upload.Phase);
        await store.ResetPartAsync(_user, fixture.Request.VersionId, 0, 0);
        await UploadPart(store, fixture, 0, 0);
        Assert.True((await store.CompleteAsync(_user, fixture.Request.VersionId)).IsCurrent);
        await Error(40901, () => store.ResetPartAsync(_user, fixture.Request.VersionId, 0, 0));
    }

    [Fact]
    public async Task DuplicateRequestsAreIdempotentButCannotRewriteInputOrAnAcknowledgedPart()
    {
        var fixture = Fixture.Create(); var store = Store();
        await store.BeginAsync(_user, fixture.Request);
        await UploadPart(store, fixture, 0, 0);
        await UploadPart(store, fixture, 0, 0);
        var changed = fixture.Request with { BaseVersionId = Guid.NewGuid() };
        await Error(40901, () => store.BeginAsync(_user, changed));
        var bytes = fixture.Content[0].ToArray(); bytes[0] ^= 1;
        await Error(40901, () => store.UploadPartAsync(_user, fixture.Request.VersionId, 0, 0, Hash(bytes), new MemoryStream(bytes)));
        await UploadAll(store, fixture);
        await store.CompleteAsync(_user, fixture.Request.VersionId);
        await UploadPart(store, fixture, 0, 0);
        Assert.Equal(fixture.Request.InputJson, (await store.GetAsync(_user, fixture.Request.VersionId)).InputJson);
    }

    [Fact]
    public async Task ConcurrentBranchesKeepBothCompleteMaterialSetsAndRequireAnExplicitHeadComparison()
    {
        var noteId = Guid.NewGuid();
        var a = Fixture.Create(noteId: noteId); var b = Fixture.Create(noteId: noteId);
        await Store().BeginAsync(_user, a.Request); await Store().BeginAsync(_user, b.Request);
        await UploadAll(Store(), a); await UploadAll(Store(), b);
        var results = await Task.WhenAll(Store().CompleteAsync(_user, a.Request.VersionId), Store().CompleteAsync(_user, b.Request.VersionId));
        Assert.Single(results.Where(value => value.IsCurrent));
        var conflict = Assert.Single(results.Where(value => value.HasConflict));
        var head = Assert.Single(results.Where(value => value.IsCurrent));
        Assert.Equal("Conflict", conflict.Phase);
        await Error(40901, () => Store().PromoteAsync(_user, conflict.VersionId, null));
        var chosen = await Store().PromoteAsync(_user, conflict.VersionId, head.VersionId);
        Assert.True(chosen.IsCurrent);
        Assert.False(chosen.HasConflict);
        var versions = await Store().ListAsync(_user, noteId);
        Assert.Equal(2, versions.Count);
        Assert.Single(versions.Where(value => value.IsCurrent));
        Assert.Equal(head.VersionId, (await Store().GetAsync(_user, head.VersionId)).Upload.VersionId);
    }

    [Fact]
    public async Task NewVersionBasedOnCurrentCanPublishWithoutChangingTheFrozenOldInput()
    {
        var first = Fixture.Create();
        await Store().BeginAsync(_user, first.Request); await UploadAll(Store(), first);
        await Store().CompleteAsync(_user, first.Request.VersionId);
        var next = Fixture.Create(noteId: first.Request.NoteId, baseId: first.Request.VersionId);
        await Store().BeginAsync(_user, next.Request); await UploadAll(Store(), next);
        Assert.True((await Store().CompleteAsync(_user, next.Request.VersionId)).IsCurrent);
        var old = await Store().GetAsync(_user, first.Request.VersionId);
        Assert.False(old.Upload.IsCurrent);
        Assert.Equal(first.Request.InputJson, old.InputJson);
    }

    [Fact]
    public async Task ClonePreservesAudioPhotoAndEditableInkWhileRequiringNewDocumentUpload()
    {
        var original = Fixture.Create(audioSamples: 160_000);
        var store = Store();
        await store.BeginAsync(_user, original.Request);
        await UploadAll(store, original);
        await store.CompleteAsync(_user, original.Request.VersionId);
        var next = CloneFixture(original);

        var cloned = await store.CloneAsync(_user, original.Request.VersionId,
            new CloneNoteMaterialVersionRequest(original.Request.VersionId, next.Request));
        Assert.Equal(next.Request.VersionId, cloned.VersionId);
        Assert.Equal(original.Request.VersionId, cloned.BaseVersionId);
        Assert.Equal(original.Request.VersionId, cloned.CurrentVersionId);
        Assert.False(cloned.IsCurrent);
        Assert.Equal("Uploading", cloned.Phase);
        Assert.Empty(cloned.Files[0].UploadedParts);
        Assert.Empty(cloned.Files[1].UploadedParts);
        Assert.All(cloned.Files.Skip(2), file => Assert.Equal(file.PartCount, file.UploadedParts.Count));
        await UploadPart(store, next, 0, 0);
        await UploadPart(store, next, 1, 0);
        var complete = await store.CompleteAsync(_user, next.Request.VersionId);
        Assert.True(complete.IsCurrent);

        var saved = await store.GetAsync(_user, next.Request.VersionId);
        Assert.Contains("续写标题", saved.InputJson);
        for (var index = 1; index < next.Content.Count; index++)
        {
            using var output = new MemoryStream();
            await store.ReadFileAsync(_user, next.Request.VersionId, index, output);
            Assert.Equal(next.Content[index], output.ToArray());
        }
        Assert.Contains("\"kind\":\"ink\"", saved.InputJson);
        Assert.Contains("\"recordings\":[", saved.InputJson);
    }

    [Fact]
    public async Task RawMaterialJsonPreservesUnknownFieldsAcrossCurrentReadAndClone()
    {
        var fixture = Fixture.Create();
        var input = JsonNode.Parse(fixture.Request.InputJson)!.AsObject();
        input["futureInputField"] = new JsonObject { ["preserved"] = true };
        var inputJson = input.ToJsonString(Json);
        var manifest = JsonNode.Parse(fixture.Request.ManifestJson)!.AsObject();
        manifest["futureManifestField"] = new JsonObject { ["preserved"] = "yes" };
        manifest["inputSha256"] = Hash(Encoding.UTF8.GetBytes(inputJson));
        var manifestJson = manifest.ToJsonString(Json);
        fixture = fixture with { Request = fixture.Request with { InputJson = inputJson, ManifestJson = manifestJson } };

        var store = Store();
        await store.BeginAsync(_user, fixture.Request);
        await UploadAll(store, fixture);
        await store.CompleteAsync(_user, fixture.Request.VersionId);

        var current = await store.GetCurrentAsync(_user, fixture.Request.NoteId);
        Assert.Equal(inputJson, current.InputJson);
        Assert.Equal(manifestJson, current.ManifestJson);

        var next = CloneFixture(fixture);
        var cloned = await store.CloneAsync(_user, fixture.Request.VersionId,
            new CloneNoteMaterialVersionRequest(fixture.Request.VersionId, next.Request));

        Assert.Equal(fixture.Request.VersionId, cloned.BaseVersionId);
        Assert.Equal(fixture.Request.VersionId, cloned.CurrentVersionId);
        var clonedVersion = await store.GetAsync(_user, next.Request.VersionId);
        Assert.Equal(next.Request.InputJson, clonedVersion.InputJson);
        Assert.Equal(next.Request.ManifestJson, clonedVersion.ManifestJson);
        Assert.Equal(true, JsonNode.Parse(clonedVersion.InputJson)! ["futureInputField"]!["preserved"]!.GetValue<bool>());
        Assert.Equal("yes", JsonNode.Parse(clonedVersion.ManifestJson)! ["futureManifestField"]!["preserved"]!.GetValue<string>());
    }

    [Fact]
    public async Task CloneRejectsCrossOwnerMissingSourceAndStaleCurrentVersion()
    {
        var original = Fixture.Create();
        var store = Store();
        await store.BeginAsync(_user, original.Request);
        await UploadAll(store, original);
        await store.CompleteAsync(_user, original.Request.VersionId);
        var next = CloneFixture(original);
        var clone = new CloneNoteMaterialVersionRequest(original.Request.VersionId, next.Request);

        await Error(40401, () => store.CloneAsync(Guid.NewGuid(), original.Request.VersionId, clone));
        await Error(40401, () => store.CloneAsync(_user, Guid.NewGuid(), clone));
        await Error(40901, () => store.CloneAsync(_user, original.Request.VersionId,
            clone with { ExpectedCurrentVersionId = Guid.NewGuid() }));
        await store.DeleteNoteAsync(_user, original.Request.NoteId);
        await Error(40401, () => store.CloneAsync(_user, original.Request.VersionId, clone));
    }

    [Fact]
    public async Task CloneDoesNotAcceptMissingOrCorruptedSourceMaterial()
    {
        var original = Fixture.Create();
        var store = Store();
        await store.BeginAsync(_user, original.Request);
        await UploadAll(store, original);
        await store.CompleteAsync(_user, original.Request.VersionId);
        var sourcePart = Path.Combine(_root, "binary", _user.ToString("D"), "note-parts",
            original.Request.VersionId.ToString("D"), Hash(Encoding.UTF8.GetBytes("2/0")) + ".blob");
        File.Delete(sourcePart);
        var next = CloneFixture(original);

        await Error(40901, () => store.CloneAsync(_user, original.Request.VersionId,
            new CloneNoteMaterialVersionRequest(original.Request.VersionId, next.Request)));
        Assert.Equal(original.Request.VersionId,
            (await store.GetAsync(_user, original.Request.VersionId)).Upload.CurrentVersionId);
    }

    [Fact]
    public async Task ClonePublishedAfterAnotherVersionBecomesCurrentIsRetainedAsConflict()
    {
        var original = Fixture.Create();
        var store = Store();
        await store.BeginAsync(_user, original.Request);
        await UploadAll(store, original);
        await store.CompleteAsync(_user, original.Request.VersionId);
        var clone = CloneFixture(original);
        await store.CloneAsync(_user, original.Request.VersionId,
            new CloneNoteMaterialVersionRequest(original.Request.VersionId, clone.Request));
        await UploadPart(store, clone, 0, 0);
        await UploadPart(store, clone, 1, 0);

        var parallel = Fixture.Create(noteId: original.Request.NoteId, baseId: original.Request.VersionId);
        await store.BeginAsync(_user, parallel.Request);
        await UploadAll(store, parallel);
        Assert.True((await store.CompleteAsync(_user, parallel.Request.VersionId)).IsCurrent);

        var result = await store.CompleteAsync(_user, clone.Request.VersionId);
        Assert.Equal("Conflict", result.Phase);
        Assert.True(result.HasConflict);
        Assert.Equal(parallel.Request.VersionId, result.CurrentVersionId);
        Assert.Equal(clone.Request.InputJson, (await store.GetAsync(_user, clone.Request.VersionId)).InputJson);
    }

    [Fact]
    public async Task AccountIsolationAppliesToReadsPartsCompletionPromotionCancellationAndDeletion()
    {
        var fixture = Fixture.Create(); var other = Guid.NewGuid();
        await Store().BeginAsync(_user, fixture.Request); await UploadAll(Store(), fixture);
        await Store().CompleteAsync(_user, fixture.Request.VersionId);
        await Error(40401, () => Store().GetAsync(other, fixture.Request.VersionId));
        await Error(40401, () => Store().CompleteAsync(other, fixture.Request.VersionId));
        await Error(40401, () => Store().PromoteAsync(other, fixture.Request.VersionId, null));
        await Error(40401, () => Store().UploadPartAsync(other, fixture.Request.VersionId, 0, 0, Hash(fixture.Content[0]), new MemoryStream(fixture.Content[0])));
        using var output = new MemoryStream();
        await Error(40401, () => Store().ReadFileAsync(other, fixture.Request.VersionId, 0, output));
        await Store().CancelAsync(other, fixture.Request.VersionId);
        await Store().DeleteNoteAsync(other, fixture.Request.NoteId);
        Assert.True((await Store().GetAsync(_user, fixture.Request.VersionId)).Upload.IsCurrent);
        Assert.Empty(await Store().ListAsync(other, fixture.Request.NoteId));
    }

    [Fact]
    public async Task ExpiredIncompleteUploadsReleaseQuotaButPublishedVersionsRemain()
    {
        var time = new MutableTime(); var store = Store(time);
        var saved = Fixture.Create(); await store.BeginAsync(_user, saved.Request); await UploadAll(store, saved);
        await store.CompleteAsync(_user, saved.Request.VersionId);
        var pending = Fixture.Create(); await store.BeginAsync(_user, pending.Request); await UploadPart(store, pending, 0, 0);
        time.Now = time.Now.AddHours(_options.UploadExpiryHours + 1);
        await Error(40401, () => store.GetAsync(_user, pending.Request.VersionId));
        Assert.True((await store.GetAsync(_user, saved.Request.VersionId)).Upload.IsCurrent);
        Assert.False(Directory.Exists(Path.Combine(_root, _user.ToString("D"), pending.Request.VersionId.ToString("D"))));
    }

    [Fact]
    public async Task ConcurrentReservationsCannotExceedTheAccountQuota()
    {
        var a = Fixture.Create(); var b = Fixture.Create();
        var capacity = a.Content.Sum(bytes => (long)bytes.Length);
        _options.MaxVersionBytes = capacity;
        _options.MaxUserReservedBytes = capacity + JsonSerializer.SerializeToUtf8Bytes(a.Request, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length + 1024;
        async Task<int> Begin(Fixture value)
        {
            try { await Store().BeginAsync(_user, value.Request); return 0; }
            catch (DomainException error) { return error.ErrorCode; }
        }
        var results = await Task.WhenAll(Begin(a), Begin(b));
        Assert.Equal(new[] { 0, 41301 }, results.Order().ToArray());
    }

    [Fact]
    public async Task CancellationAndWholeNoteDeletionRemoveServerMaterialsWithoutAffectingOtherNotes()
    {
        var pending = Fixture.Create(); await Store().BeginAsync(_user, pending.Request); await UploadPart(Store(), pending, 0, 0);
        await Store().CancelAsync(_user, pending.Request.VersionId);
        await Error(40401, () => Store().GetAsync(_user, pending.Request.VersionId));
        var saved = Fixture.Create(); await Store().BeginAsync(_user, saved.Request); await UploadAll(Store(), saved);
        await Store().CompleteAsync(_user, saved.Request.VersionId);
        await Error(40901, () => Store().CancelAsync(_user, saved.Request.VersionId));
        var separate = Fixture.Create(); await Store().BeginAsync(_user, separate.Request);
        await Store().DeleteNoteAsync(_user, saved.Request.NoteId);
        await Error(40401, () => Store().GetAsync(_user, saved.Request.VersionId));
        Assert.Equal(separate.Request.VersionId, (await Store().GetAsync(_user, separate.Request.VersionId)).Upload.VersionId);
    }

    [Fact]
    public async Task OversizedWrongHashWrongLengthAndWrongIndexPartsCannotBeAcknowledged()
    {
        var fixture = Fixture.Create(); await Store().BeginAsync(_user, fixture.Request);
        var bytes = fixture.Content[0];
        await Error(40001, () => Store().UploadPartAsync(_user, fixture.Request.VersionId, 0, 0, new string('0', 64), new MemoryStream(bytes)));
        await Error(40001, () => Store().UploadPartAsync(_user, fixture.Request.VersionId, 0, 0, Hash([]), new MemoryStream()));
        await Error(40401, () => Store().UploadPartAsync(_user, fixture.Request.VersionId, -1, 0, Hash(bytes), new MemoryStream(bytes)));
        var huge = new byte[NoteMaterialUploadOptions.PartSizeBytes + 1];
        await Error(41301, () => Store().UploadPartAsync(_user, fixture.Request.VersionId, 0, 0, Hash(huge), new MemoryStream(huge)));
        Assert.Empty((await Store().GetAsync(_user, fixture.Request.VersionId)).Upload.Files[0].UploadedParts);
    }

    [Fact]
    public async Task MissingConsentIncompleteManifestChangedInputAndPathInjectionAreRejected()
    {
        var fixture = Fixture.Create();
        await Error(40001, () => Store().BeginAsync(_user, fixture.Request with { StorageConsent = false }));
        var manifest = JsonNode.Parse(fixture.Request.ManifestJson)!.AsObject();
        manifest["files"]!.AsArray().RemoveAt(2);
        await Error(40001, () => Store().BeginAsync(_user, fixture.Request with { ManifestJson = manifest.ToJsonString(Json) }));
        manifest = JsonNode.Parse(fixture.Request.ManifestJson)!.AsObject();
        manifest["files"]![0]!["path"] = "../../outside";
        await Error(40001, () => Store().BeginAsync(_user, fixture.Request with { ManifestJson = manifest.ToJsonString(Json) }));
        await Error(40001, () => Store().BeginAsync(_user, fixture.Request with { InputJson = fixture.Request.InputJson + " " }));
        await Error(40001, () => Store().BeginAsync(_user, fixture.Request with { ManifestJson = "{\"schemaVersion\":1,\"schemaVersion\":1}" }));
    }

    [Fact]
    public async Task HashCorrectButInvalidWavCannotPublish()
    {
        var fixture = Fixture.Create();
        fixture.Content[^1][0] ^= 1;
        var manifest = JsonNode.Parse(fixture.Request.ManifestJson)!.AsObject();
        manifest["files"]!.AsArray()[^1]!["sha256"] = Hash(fixture.Content[^1]);
        fixture = fixture with { Request = fixture.Request with { ManifestJson = manifest.ToJsonString(Json) } };
        await Store().BeginAsync(_user, fixture.Request); await UploadAll(Store(), fixture);
        await Error(40001, () => Store().CompleteAsync(_user, fixture.Request.VersionId));
    }

    [Fact]
    public async Task LoweringNewUploadLimitsDoesNotMakeExistingVersionsUnreadableOrUnresumable()
    {
        var fixture = Fixture.Create(); await Store().BeginAsync(_user, fixture.Request); await UploadAll(Store(), fixture);
        _options.MaxVersionBytes = 1; _options.MaxFiles = 2;
        Assert.Equal(fixture.Request.VersionId, (await Store().BeginAsync(_user, fixture.Request)).VersionId);
        Assert.True((await Store().CompleteAsync(_user, fixture.Request.VersionId)).IsCurrent);
        Assert.Equal(fixture.Request.InputJson, (await Store().GetAsync(_user, fixture.Request.VersionId)).InputJson);
        await Error(41301, () => Store().BeginAsync(_user, Fixture.Create().Request));
    }

    [Fact]
    public async Task DurableDeletionIntentIsFinishedWhenTheStoreReopens()
    {
        var fixture = Fixture.Create(); await Store().BeginAsync(_user, fixture.Request); await UploadPart(Store(), fixture, 0, 0);
        var account = Path.Combine(_root, _user.ToString("D")); var ledgerPath = Path.Combine(account, "ledger.json");
        var ledger = JsonNode.Parse(await File.ReadAllTextAsync(ledgerPath))!.AsObject();
        ledger["versions"]!.AsObject().Remove(fixture.Request.VersionId.ToString("D"));
        ledger["deletions"]!.AsArray().Add(fixture.Request.VersionId.ToString("D"));
        await File.WriteAllTextAsync(ledgerPath, ledger.ToJsonString(Json));
        // This fixture targets the persisted intent/before-removal window; it is not a real power-loss test.
        await Error(40401, () => Store().GetAsync(_user, fixture.Request.VersionId));
        Assert.False(Directory.Exists(Path.Combine(account, fixture.Request.VersionId.ToString("D"))));
        ledger = JsonNode.Parse(await File.ReadAllTextAsync(ledgerPath))!.AsObject();
        Assert.Empty(ledger["deletions"]!.AsArray());
    }

    [Fact]
    public async Task HeaderPersistedBeforeLedgerPublicationRecoversTheSameParts()
    {
        var fixture = Fixture.Create(); await Store().BeginAsync(_user, fixture.Request); await UploadPart(Store(), fixture, 0, 0);
        var ledgerPath = Path.Combine(_root, _user.ToString("D"), "ledger.json");
        var ledger = JsonNode.Parse(await File.ReadAllTextAsync(ledgerPath))!.AsObject();
        ledger["versions"]!.AsObject().Remove(fixture.Request.VersionId.ToString("D"));
        await File.WriteAllTextAsync(ledgerPath, ledger.ToJsonString(Json));
        var resumed = await Store().BeginAsync(_user, fixture.Request);
        Assert.Equal(new[] { 0 }, resumed.Files[0].UploadedParts);
        await UploadAll(Store(), fixture);
        Assert.True((await Store().CompleteAsync(_user, fixture.Request.VersionId)).IsCurrent);
    }

    [Fact]
    public async Task Legacy_wav_material_without_a_format_field_still_slices_to_the_same_bytes()
    {
        var fixture = Fixture.Create(audioSamples: 160);
        var store = Store();
        await store.BeginAsync(_user, fixture.Request);
        await UploadAll(store, fixture);
        await store.CompleteAsync(_user, fixture.Request.VersionId);

        var slice = await store.ReadAudioSliceAsync(_user, fixture.Request.VersionId, 5, 0, 160,
            CancellationToken.None);

        Assert.Equal(fixture.Content[5], slice);
        Assert.Equal(".wav", Path.GetExtension((await store.GetAsync(_user, fixture.Request.VersionId))
            .Upload.Files[5].Path));
    }

    [Fact]
    public async Task Wav_slicing_ignores_invalid_opus_decoder_configuration()
    {
        var fixture = Fixture.Create(audioSamples: 160);
        var store = Store();
        await store.BeginAsync(_user, fixture.Request);
        await UploadAll(store, fixture);
        await store.CompleteAsync(_user, fixture.Request.VersionId);
        var options = new NoteMaterialUploadOptions
        {
            StorageRoot = _root, MaxVersionBytes = 32 * 1024 * 1024,
            MaxUserReservedBytes = 64 * 1024 * 1024, FfmpegPath = " ",
            AudioDecodeTimeoutSeconds = 0
        };

        var slice = await Store(options: options).ReadAudioSliceAsync(_user,
            fixture.Request.VersionId, 5, 0, 160, CancellationToken.None);

        Assert.Equal(fixture.Content[5], slice);
    }

    [Fact]
    public async Task Opus_upload_and_download_keep_the_original_ogg_container()
    {
        var fixture = OpusFixture();
        var store = Store();
        var begun = await store.BeginAsync(_user, fixture.Request);
        Assert.EndsWith(".opus", begun.Files[5].Path);
        await UploadAll(store, fixture);

        var completed = await store.CompleteAsync(_user, fixture.Request.VersionId);
        Assert.Equal("Uploaded", completed.Phase);
        Assert.True(completed.IsCurrent);

        using var output = new MemoryStream();
        await store.ReadFileAsync(_user, fixture.Request.VersionId, 5, output);
        Assert.Equal(fixture.Content[5], output.ToArray());
    }

    [Fact]
    public async Task Opus_clone_preserves_the_original_container_and_rejects_a_wav_relabel()
    {
        var original = OpusFixture();
        var store = Store();
        await store.BeginAsync(_user, original.Request);
        await UploadAll(store, original);
        await store.CompleteAsync(_user, original.Request.VersionId);
        var next = CloneFixture(original);

        var cloned = await store.CloneAsync(_user, original.Request.VersionId,
            new CloneNoteMaterialVersionRequest(original.Request.VersionId, next.Request));
        Assert.EndsWith(".opus", cloned.Files[5].Path);
        Assert.Equal(cloned.Files[5].PartCount, cloned.Files[5].UploadedParts.Count);
        await UploadPart(store, next, 0, 0);
        await UploadPart(store, next, 1, 0);
        Assert.True((await store.CompleteAsync(_user, next.Request.VersionId)).IsCurrent);
        using (var output = new MemoryStream())
        {
            await store.ReadFileAsync(_user, next.Request.VersionId, 5, output);
            Assert.Equal(original.Content[5], output.ToArray());
        }

        var relabelled = RewriteInput(next, input =>
        {
            AudioChunk(input)["format"] = "wav";
            AudioChunk(input)["mimeType"] = "audio/wav";
        });
        await Error(40001, () => store.CloneAsync(_user, original.Request.VersionId,
            new CloneNoteMaterialVersionRequest(original.Request.VersionId, relabelled.Request)));
    }

    [Fact]
    public async Task Opus_mime_without_format_still_uses_the_canonical_opus_path()
    {
        var fixture = Fixture.Create(audioSamples: 1600, audio: TinyOpus(),
            audioFormat: "opus", includeFormat: false);
        var store = Store();
        var begun = await store.BeginAsync(_user, fixture.Request);
        Assert.EndsWith(".opus", begun.Files[5].Path);
        await UploadAll(store, fixture);
        Assert.Equal("Uploaded",
            (await store.CompleteAsync(_user, fixture.Request.VersionId)).Phase);
    }

    [Fact]
    public async Task Opus_with_truncated_container_or_wrong_sample_count_never_publishes()
    {
        await AssertOpusRejected(Fixture.Create(audioSamples: 1600, audio: TinyOpus()[..^1],
            audioFormat: "opus"));
        await AssertOpusRejected(Fixture.Create(audioSamples: 1599, audio: TinyOpus(),
            audioFormat: "opus"));
        await AssertOpusRejected(Fixture.Create(audioSamples: 1600,
            audio: Encoding.UTF8.GetBytes("this is not an ogg opus recording"), audioFormat: "opus"));
    }

    [Fact]
    public async Task Opus_with_corrupt_crc_or_inconsistent_pages_never_publishes()
    {
        var corruptCrc = TinyOpus();
        corruptCrc[^1] ^= 1;
        await AssertOpusRejected(Fixture.Create(audioSamples: 1600, audio: corruptCrc,
            audioFormat: "opus"));

        const uint serial = 0x12345678;
        var gapped = OggOpusTestData.Concat([
            OggOpusTestData.Page(0x02, serial, 0, 0, OggOpusTestData.OpusHead()),
            OggOpusTestData.Page(0, serial, 2, 0, OggOpusTestData.OpusTags()),
            OggOpusTestData.Page(0x04, serial, 3, 5112, OggOpusTestData.AudioPacket())
        ]);
        await AssertOpusRejected(Fixture.Create(audioSamples: 1600, audio: gapped,
            audioFormat: "opus"));
    }

    [Fact]
    public async Task Opus_manifest_rejects_unknown_conflicting_zero_sample_and_duplicate_chunks_at_begin()
    {
        var unknown = RewriteInput(OpusFixture(), input => AudioChunk(input)["format"] = "mp3");
        await Error(40001, () => Store().BeginAsync(_user, unknown.Request));

        var conflict = RewriteInput(OpusFixture(), input => AudioChunk(input)["format"] = "wav");
        await Error(40001, () => Store().BeginAsync(_user, conflict.Request));

        var zero = RewriteInput(OpusFixture(), input => AudioChunk(input)["samples"] = 0);
        await Error(40001, () => Store().BeginAsync(_user, zero.Request));

        var duplicate = RewriteInput(OpusFixture(), input =>
        {
            var chunks = input["recordings"]!.AsArray()[0]!["chunks"]!.AsArray();
            var clone = AudioChunk(input).DeepClone().AsObject();
            clone["format"] = "wav";
            clone["mimeType"] = "audio/wav";
            chunks.Add(clone);
        });
        await Error(40001, () => Store().BeginAsync(_user, duplicate.Request));
    }

    [Fact]
    public async Task Opus_manifest_rejects_files_over_the_64_mib_declared_boundary_at_begin()
    {
        var fixture = OpusFixture();
        var manifest = JsonNode.Parse(fixture.Request.ManifestJson)!.AsObject();
        manifest["files"]!.AsArray()[5]!["sizeBytes"] = NoteAudioFormats.MaxOpusFileBytes + 1;
        var request = fixture.Request with { ManifestJson = manifest.ToJsonString(Json) };
        var options = new NoteMaterialUploadOptions
        {
            StorageRoot = _root, MaxVersionBytes = 128 * 1024 * 1024,
            MaxUserReservedBytes = 128 * 1024 * 1024
        };

        await Error(40001, () => Store(options: options).BeginAsync(_user, request));
    }

    [Fact]
    public async Task Missing_opus_decoder_fails_with_safe_50303_and_keeps_the_original_downloadable()
    {
        var fixture = OpusFixture();
        var store = Store();
        await store.BeginAsync(_user, fixture.Request);
        await UploadAll(store, fixture);
        await store.CompleteAsync(_user, fixture.Request.VersionId);

        var broken = new NoteMaterialUploadOptions
        {
            StorageRoot = _root, MaxVersionBytes = 32 * 1024 * 1024,
            MaxUserReservedBytes = 64 * 1024 * 1024,
            FfmpegPath = Path.Combine(_root, "missing-ffmpeg")
        };
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            Store(options: broken).ReadAudioSliceAsync(_user, fixture.Request.VersionId, 5, 0, 1600,
                CancellationToken.None));
        Assert.Equal(50303, error.ErrorCode);
        Assert.DoesNotContain(_root, error.Message);
        Assert.DoesNotContain("missing-ffmpeg", error.Message);

        using var output = new MemoryStream();
        await Store().ReadFileAsync(_user, fixture.Request.VersionId, 5, output);
        Assert.Equal(fixture.Content[5], output.ToArray());
    }

    [Fact]
    public async Task Batch_generation_fails_before_provider_with_safe_50303_when_opus_decoder_is_missing()
    {
        var fixture = OpusFixture();
        var store = Store();
        await store.BeginAsync(_user, fixture.Request);
        await UploadAll(store, fixture);
        await store.CompleteAsync(_user, fixture.Request.VersionId);
        var version = await store.GetAsync(_user, fixture.Request.VersionId);
        var steps = NoteAsrPlan.Build(version, NoteAsrPlan.SamplesPerCall);
        var broken = new NoteMaterialUploadOptions
        {
            StorageRoot = _root, MaxVersionBytes = 32 * 1024 * 1024,
            MaxUserReservedBytes = 64 * 1024 * 1024,
            FfmpegPath = Path.Combine(_root, "missing-ffmpeg")
        };

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            NoteAsrBatchJsonlReader.GenerateFromPlanAsync(_user, fixture.Request.VersionId, steps,
                NoteAsrPlan.SamplesPerCall, "zh", "mimo-v2.6-flash", "逐字转写",
                Store(options: broken), 4 * 1024 * 1024, ["seg-0"], [0], 4096,
                CancellationToken.None));

        Assert.Equal(50303, error.ErrorCode);
        Assert.DoesNotContain(_root, error.Message);
        Assert.DoesNotContain("missing-ffmpeg", error.Message);
    }

    [SkippableFact]
    public async Task Opus_slice_is_decoded_to_exact_16k_wav_and_recovers_after_the_decoder_is_fixed()
    {
        Skip.IfNot(FfmpegAvailable(), "FFmpeg is required to verify the Opus decode boundary");
        var fixture = OpusFixture();
        var store = Store();
        await store.BeginAsync(_user, fixture.Request);
        await UploadAll(store, fixture);
        await store.CompleteAsync(_user, fixture.Request.VersionId);

        var broken = new NoteMaterialUploadOptions
        {
            StorageRoot = _root, MaxVersionBytes = 32 * 1024 * 1024,
            MaxUserReservedBytes = 64 * 1024 * 1024,
            FfmpegPath = Path.Combine(_root, "missing-ffmpeg")
        };
        await Error(50303, () => Store(options: broken).ReadAudioSliceAsync(_user,
            fixture.Request.VersionId, 5, 0, 1600, CancellationToken.None));

        var decoded = Store();
        var full = await decoded.ReadAudioSliceAsync(_user, fixture.Request.VersionId, 5, 0, 1600,
            CancellationToken.None);
        AssertWav(full, 1600);
        Assert.Contains(full.AsSpan(44).ToArray(), value => value != 0);

        var offset = await decoded.ReadAudioSliceAsync(_user, fixture.Request.VersionId, 5, 800, 800,
            CancellationToken.None);
        AssertWav(offset, 800);
        Assert.Equal(full.AsSpan(44 + 800 * 2, 800 * 2).ToArray(), offset.AsSpan(44).ToArray());

        using var output = new MemoryStream();
        await decoded.ReadFileAsync(_user, fixture.Request.VersionId, 5, output);
        Assert.Equal(fixture.Content[5], output.ToArray());
    }

    private NoteMaterialUploadStore Store(TimeProvider? time = null, NoteMaterialUploadOptions? options = null) =>
        new(Options.Create(options ?? _options), time);

    private async Task AssertOpusRejected(Fixture fixture)
    {
        var store = Store();
        await store.BeginAsync(_user, fixture.Request);
        await UploadAll(store, fixture);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            store.CompleteAsync(_user, fixture.Request.VersionId));
        Assert.Equal(40001, error.ErrorCode);
        Assert.DoesNotContain(_root, error.Message);
        Assert.Equal("Uploading", (await store.GetAsync(_user, fixture.Request.VersionId)).Upload.Phase);
    }

    private static Fixture OpusFixture() => Fixture.Create(audioSamples: 1600, audio: TinyOpus(),
        audioFormat: "opus");

    private static byte[] TinyOpus() => OggOpusTestData.TinyOpus();

    private static Fixture RewriteInput(Fixture fixture, Action<JsonObject> edit)
    {
        var input = JsonNode.Parse(fixture.Request.InputJson)!.AsObject();
        edit(input);
        var inputJson = input.ToJsonString(Json);
        var manifest = JsonNode.Parse(fixture.Request.ManifestJson)!.AsObject();
        manifest["inputSha256"] = Hash(Encoding.UTF8.GetBytes(inputJson));
        return fixture with
        {
            Request = fixture.Request with
            {
                InputJson = inputJson, ManifestJson = manifest.ToJsonString(Json)
            }
        };
    }

    private static JsonObject AudioChunk(JsonObject input) =>
        input["recordings"]!.AsArray()[0]!["chunks"]!.AsArray()[0]!.AsObject();

    private static void AssertWav(byte[] bytes, int samples)
    {
        Assert.Equal(44 + samples * 2, bytes.Length);
        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        Assert.Equal("WAVEfmt "u8.ToArray(), bytes.AsSpan(8, 8).ToArray());
        Assert.Equal(16_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(32_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28)));
        Assert.Equal((uint)(samples * 2), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
    }

    private static bool FfmpegAvailable()
    {
        try
        {
            var start = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            start.ArgumentList.Add("-version");
            using var process = Process.Start(start);
            if (process is null) return false;
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task Error(int code, Func<Task> action) => Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(action)).ErrorCode);
    private async Task UploadAll(NoteMaterialUploadStore store, Fixture fixture)
    {
        for (var file = 0; file < fixture.Content.Count; file++)
            for (var part = 0; part < (fixture.Content[file].Length + NoteMaterialUploadOptions.PartSizeBytes - 1) / NoteMaterialUploadOptions.PartSizeBytes; part++)
                await UploadPart(store, fixture, file, part);
    }
    private async Task UploadPart(NoteMaterialUploadStore store, Fixture fixture, int file, int part)
    {
        var bytes = fixture.Content[file]; var start = part * NoteMaterialUploadOptions.PartSizeBytes;
        var chunk = bytes.AsSpan(start, Math.Min(NoteMaterialUploadOptions.PartSizeBytes, bytes.Length - start)).ToArray();
        await store.UploadPartAsync(_user, fixture.Request.VersionId, file, part, Hash(chunk), new MemoryStream(chunk));
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static Fixture CloneFixture(Fixture original)
    {
        var nextVersionId = Guid.NewGuid();
        var input = JsonNode.Parse(original.Request.InputJson)!.AsObject();
        input["id"] = nextVersionId.ToString("D");
        var note = input["note"]!.AsObject();
        note["title"] = "续写标题";
        note["revision"] = note["revision"]!.GetValue<int>() + 1;
        note["blocks"]![0]!["text"] = "## 研究方法\n\n续写正文。";
        var noteJson = note.ToJsonString(Json);
        var content = original.Content.Select(bytes => bytes.ToArray()).ToList();
        content[0] = Encoding.UTF8.GetBytes(noteJson);
        var manifest = JsonNode.Parse(original.Request.ManifestJson)!.AsObject();
        manifest["versionId"] = nextVersionId.ToString("D");
        manifest["revision"] = note["revision"]!.GetValue<int>();
        manifest["inputSha256"] = Hash(Encoding.UTF8.GetBytes(input.ToJsonString(Json)));
        manifest["files"]![0]!["sha256"] = Hash(content[0]);
        manifest["files"]![0]!["sizeBytes"] = content[0].LongLength;
        using (var parsedManifest = JsonDocument.Parse(original.Request.ManifestJson))
        {
            var photoPath = parsedManifest.RootElement.GetProperty("files")[2].GetProperty("path").GetString();
            var inkPath = parsedManifest.RootElement.GetProperty("files")[3].GetProperty("path").GetString();
            content[1] = Encoding.UTF8.GetBytes($"## 研究方法\n\n续写正文。\n\n![照片]({photoPath})\n\n![手写卡片]({inkPath})\n<!-- 可编辑笔迹、身份和尺寸保存在同包 note.json 中 -->");
        }
        manifest["files"]![1]!["sha256"] = Hash(content[1]);
        manifest["files"]![1]!["sizeBytes"] = content[1].LongLength;
        var request = original.Request with
        {
            VersionId = nextVersionId,
            BaseVersionId = original.Request.VersionId,
            InputJson = input.ToJsonString(Json),
            ManifestJson = manifest.ToJsonString(Json),
        };
        return new(request, content);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    private sealed class MutableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-26T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed record Fixture(BeginNoteMaterialUploadRequest Request, List<byte[]> Content)
    {
        internal static Fixture Create(int audioSamples = 160, Guid? noteId = null, Guid? baseId = null,
            byte[]? audio = null, string? audioFormat = null, bool includeFormat = true)
        {
            var version = Guid.NewGuid(); var note = noteId ?? Guid.NewGuid(); var recording = Guid.NewGuid();
            var photoBlock = Guid.NewGuid(); var photo = Guid.NewGuid(); var inkBlock = Guid.NewGuid();
            var opus = string.Equals(audioFormat, "opus", StringComparison.Ordinal);
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a8X8AAAAASUVORK5CYII=");
            var ink = new { width = 320.0, height = 180.0, strokes = Array.Empty<object>() };
            var inkJson = JsonSerializer.Serialize(ink, Json); var inkPath = $"assets/{inkBlock:D}-{Hash(Encoding.UTF8.GetBytes(inkJson))}.png";
            var text = "## 研究方法\n\n保留全部课堂材料。";
            var noteJson = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, id = note.ToString("D"), title = "研究方法", revision = 1, createdAtMillis = 1,
                blocks = new object[]
                {
                    new { id = Guid.NewGuid().ToString("D"), kind = "markdown", text },
                    new { id = photoBlock.ToString("D"), kind = "photo", assetId = photo.ToString("D"), mimeType = "image/png", sha256 = Hash(png) },
                    new { id = inkBlock.ToString("D"), kind = "ink", ink }
                }
            }, Json);
            var chunk = new Dictionary<string, object?>
            {
                ["segment"] = 1, ["index"] = 1, ["samples"] = audioSamples
            };
            if (opus)
            {
                if (includeFormat) chunk["format"] = "opus";
                chunk["mimeType"] = "audio/ogg";
            }
            var sessionJson = JsonSerializer.Serialize(new
            {
                id = recording.ToString("D"), schemaVersion = 1, createdAtMillis = 1, noteId = note.ToString("D"),
                phase = "Completed", currentSegment = 1, problem = (string?)null,
                chunks = new[] { chunk }
            }, Json);
            var input = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, id = version.ToString("D"), createdAtMillis = 1,
                note = JsonSerializer.Deserialize<JsonElement>(noteJson),
                recordings = new[] { JsonSerializer.Deserialize<JsonElement>(sessionJson) }
            }, Json);
            var markdown = $"{text}\n\n![照片](assets/{photo:D}.png)\n\n![手写卡片]({inkPath})\n<!-- 可编辑笔迹、身份和尺寸保存在同包 note.json 中 -->";
            var audioBytes = audio ?? Wav(audioSamples);
            var content = new List<byte[]> { Encoding.UTF8.GetBytes(noteJson), Encoding.UTF8.GetBytes(markdown), png, png,
                Encoding.UTF8.GetBytes(sessionJson), audioBytes };
            var extension = opus ? ".opus" : ".wav";
            var paths = new[] { "note.json", "document.md", $"assets/{photo:D}.png", inkPath,
                $"recordings/{recording:D}/manifest.json", $"recordings/{recording:D}/s0001-c000001{extension}" };
            var kinds = new[] { "Document", "Markdown", "Photo", "InkPreview", "RecordingManifest", "AudioChunk" };
            var files = paths.Select((path, i) => new
            {
                path, kind = kinds[i], sha256 = Hash(content[i]), sizeBytes = content[i].Length,
                blockId = i == 2 ? photoBlock.ToString("D") : i == 3 ? inkBlock.ToString("D") : null,
                recordingId = i >= 4 ? recording.ToString("D") : null,
                segment = i == 5 ? (int?)1 : null, chunkIndex = i == 5 ? (int?)1 : null, samples = i == 5 ? (long?)audioSamples : null
            }).ToArray();
            var manifest = JsonSerializer.Serialize(new { schemaVersion = 1, versionId = version.ToString("D"), noteId = note.ToString("D"),
                revision = 1, inputSha256 = Hash(Encoding.UTF8.GetBytes(input)), files }, Json);
            return new(new(version, note, baseId, input, manifest, true), content);
        }

        private static byte[] Wav(int audioSamples)
        {
            var wav = new byte[44 + audioSamples * 2];
            "RIFF"u8.CopyTo(wav); BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(wav.Length - 8));
            "WAVEfmt "u8.CopyTo(wav.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), 16_000); BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), 32_000);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 2); BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
            "data"u8.CopyTo(wav.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), (uint)(audioSamples * 2));
            return wav;
        }
    }
}
