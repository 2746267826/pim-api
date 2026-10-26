using System.Buffers.Binary;
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

    private NoteMaterialUploadStore Store(TimeProvider? time = null) => new(Options.Create(_options), time);
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
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    private sealed class MutableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-26T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed record Fixture(BeginNoteMaterialUploadRequest Request, List<byte[]> Content)
    {
        internal static Fixture Create(int audioSamples = 160, Guid? noteId = null, Guid? baseId = null)
        {
            var version = Guid.NewGuid(); var note = noteId ?? Guid.NewGuid(); var recording = Guid.NewGuid();
            var photoBlock = Guid.NewGuid(); var photo = Guid.NewGuid(); var inkBlock = Guid.NewGuid();
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
            var sessionJson = JsonSerializer.Serialize(new
            {
                id = recording.ToString("D"), schemaVersion = 1, createdAtMillis = 1, noteId = note.ToString("D"),
                phase = "Completed", currentSegment = 1, problem = (string?)null,
                chunks = new[] { new { segment = 1, index = 1, samples = audioSamples } }
            }, Json);
            var input = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, id = version.ToString("D"), createdAtMillis = 1,
                note = JsonSerializer.Deserialize<JsonElement>(noteJson),
                recordings = new[] { JsonSerializer.Deserialize<JsonElement>(sessionJson) }
            }, Json);
            var markdown = $"{text}\n\n![照片](assets/{photo:D}.png)\n\n![手写卡片]({inkPath})\n<!-- 可编辑笔迹、身份和尺寸保存在同包 note.json 中 -->";
            var wav = new byte[44 + audioSamples * 2];
            "RIFF"u8.CopyTo(wav); BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(wav.Length - 8));
            "WAVEfmt "u8.CopyTo(wav.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), 16_000); BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), 32_000);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 2); BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
            "data"u8.CopyTo(wav.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), (uint)(audioSamples * 2));
            var content = new List<byte[]> { Encoding.UTF8.GetBytes(noteJson), Encoding.UTF8.GetBytes(markdown), png, png,
                Encoding.UTF8.GetBytes(sessionJson), wav };
            var paths = new[] { "note.json", "document.md", $"assets/{photo:D}.png", inkPath,
                $"recordings/{recording:D}/manifest.json", $"recordings/{recording:D}/s0001-c000001.wav" };
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
    }
}
