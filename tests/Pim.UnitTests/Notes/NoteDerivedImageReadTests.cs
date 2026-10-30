using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Notes;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteDerivedImageReadTests : IDisposable
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a8X8AAAAASUVORK5CYII=");
    private static readonly byte[] Webp = CreateWebp();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pim-derived-image-tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _owner = Guid.NewGuid();
    private readonly NoteMaterialUploadOptions _options;

    public NoteDerivedImageReadTests() => _options = new()
    {
        StorageRoot = _root, MaxVersionBytes = 40 * 1024 * 1024, MaxUserReservedBytes = 96 * 1024 * 1024
    };

    [Fact]
    public async Task CommittedPhotoAndInkPreviewReturnExactBytesWithoutTranscoding()
    {
        var fixture = await Commit(Fixture.Create());
        var store = Store();

        Assert.Equal(Png, await store.ReadImageBytesAsync(_owner, fixture.Request.VersionId, fixture.PhotoIndex, default));
        Assert.Equal(Png, await store.ReadImageBytesAsync(_owner, fixture.Request.VersionId, fixture.InkIndex, default));
        var other = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            store.ReadImageBytesAsync(other, fixture.Request.VersionId, fixture.PhotoIndex, default));
        Assert.Equal(40401, error.ErrorCode);
    }

    [Fact]
    public async Task PendingNonImageAndWebpMaterialsAreRejected()
    {
        var pending = Fixture.Create();
        var store = Store();
        await store.BeginAsync(_owner, pending.Request);
        await UploadAll(store, pending);
        Assert.Equal(40901, (await Assert.ThrowsAsync<DomainException>(() =>
            store.ReadImageBytesAsync(_owner, pending.Request.VersionId, pending.PhotoIndex, default))).ErrorCode);

        var committed = await Commit(Fixture.Create());
        Assert.Equal(40001, (await Assert.ThrowsAsync<DomainException>(() =>
            Store().ReadImageBytesAsync(_owner, committed.Request.VersionId, 1, default))).ErrorCode);

        var webp = await Commit(Fixture.Create(photoExtension: "webp", photoBytes: Webp));
        Assert.Equal(40001, (await Assert.ThrowsAsync<DomainException>(() =>
            Store().ReadImageBytesAsync(_owner, webp.Request.VersionId, webp.PhotoIndex, default))).ErrorCode);
    }

    [Fact]
    public async Task OversizedUploadRemainsCommittedButCannotEnterVisionCall()
    {
        var photo = new byte[4 * 1024 * 1024 + 1];
        Png.CopyTo(photo, 0);
        var fixture = await Commit(Fixture.Create(photoBytes: photo));

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            Store().ReadImageBytesAsync(_owner, fixture.Request.VersionId, fixture.PhotoIndex, default));

        Assert.Equal(41301, error.ErrorCode);
        Assert.Equal("Uploaded", (await Store().GetAsync(_owner, fixture.Request.VersionId)).Upload.Phase);
    }

    [Fact]
    public async Task TamperedCommittedImageIsNeverReturnedAsOriginal()
    {
        var fixture = await Commit(Fixture.Create());
        var path = Path.Combine(_root, "binary", _owner.ToString("D"), "note-parts",
            fixture.Request.VersionId.ToString("D"), Digest(Encoding.UTF8.GetBytes($"{fixture.PhotoIndex}/0")) + ".blob");
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            Store().ReadImageBytesAsync(_owner, fixture.Request.VersionId, fixture.PhotoIndex, default));

        Assert.Equal(40001, error.ErrorCode);
    }

    [Fact]
    public async Task DerivedSourceListingCanIncludeCommittedVersionsWithoutRecordings()
    {
        var audio = await Commit(Fixture.Create(withAudio: true));
        var textOnly = await Commit(Fixture.Create(withAudio: false));

        var historical = await Store().ListAsrSourcesAsync(_owner, 0, 50, default);
        var asr = await Store().ListAsrSourcesAsync(_owner, 0, 50, default, includeWithoutAudio: false);
        var derived = await Store().ListAsrSourcesAsync(_owner, 0, 50, default, includeWithoutAudio: true);

        Assert.Contains(historical.Items, x => x.VersionId == textOnly.Request.VersionId);
        Assert.Equal(2, historical.Total);
        Assert.DoesNotContain(asr.Items, x => x.VersionId == textOnly.Request.VersionId);
        Assert.Equal(1, asr.Total);
        var textSource = Assert.Single(derived.Items, x => x.VersionId == textOnly.Request.VersionId);
        Assert.Equal(0, textSource.Recordings);
        Assert.Equal(0, textSource.TotalSamples);
        Assert.Equal(2, derived.Total);
        Assert.Contains(derived.Items, x => x.VersionId == audio.Request.VersionId);
    }

    private NoteMaterialUploadStore Store() => new(Options.Create(_options));

    private async Task<Fixture> Commit(Fixture fixture)
    {
        var store = Store();
        await store.BeginAsync(_owner, fixture.Request);
        await UploadAll(store, fixture);
        var result = await store.CompleteAsync(_owner, fixture.Request.VersionId);
        Assert.Equal("Uploaded", result.Phase);
        return fixture;
    }

    private async Task UploadAll(NoteMaterialUploadStore store, Fixture fixture)
    {
        for (var file = 0; file < fixture.Content.Count; file++)
        {
            var bytes = fixture.Content[file];
            for (var part = 0; part < (bytes.Length + NoteMaterialUploadOptions.PartSizeBytes - 1) /
                 NoteMaterialUploadOptions.PartSizeBytes; part++)
            {
                var start = part * NoteMaterialUploadOptions.PartSizeBytes;
                var chunk = bytes.AsSpan(start, Math.Min(NoteMaterialUploadOptions.PartSizeBytes, bytes.Length - start)).ToArray();
                await store.UploadPartAsync(_owner, fixture.Request.VersionId, file, part, Digest(chunk), new MemoryStream(chunk));
            }
        }
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static byte[] CreateWebp()
    {
        var bytes = new byte[13];
        "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 4);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        return bytes;
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private sealed record Fixture(BeginNoteMaterialUploadRequest Request, List<byte[]> Content, int PhotoIndex, int InkIndex)
    {
        public static Fixture Create(bool withAudio = true, string photoExtension = "png", byte[]? photoBytes = null)
        {
            var version = Guid.NewGuid(); var note = Guid.NewGuid(); var recording = Guid.NewGuid();
            var photoBlock = Guid.NewGuid(); var photo = Guid.NewGuid(); var inkBlock = Guid.NewGuid();
            var photoContent = photoBytes ?? Png;
            var photoMime = photoExtension == "webp" ? "image/webp" : "image/png";
            var text = "Derived material fixture";
            var ink = new { width = 320.0, height = 180.0, strokes = Array.Empty<object>() };
            var inkJson = JsonSerializer.Serialize(ink); var inkPath = $"assets/{inkBlock:D}-{Digest(Encoding.UTF8.GetBytes(inkJson))}.png";
            var noteJson = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, id = note.ToString("D"), title = "Derived note", revision = 1, createdAtMillis = 1,
                blocks = new object[]
                {
                    new { id = Guid.NewGuid().ToString("D"), kind = "markdown", text },
                    new { id = photoBlock.ToString("D"), kind = "photo", assetId = photo.ToString("D"), mimeType = photoMime, sha256 = Digest(photoContent) },
                    new { id = inkBlock.ToString("D"), kind = "ink", ink }
                }
            });
            var sessionJson = JsonSerializer.Serialize(new
            {
                id = recording.ToString("D"), schemaVersion = 1, createdAtMillis = 1, noteId = note.ToString("D"),
                phase = "Completed", currentSegment = 1, problem = (string?)null,
                chunks = new[] { new { segment = 1, index = 1, samples = 160 } }
            });
            var recordings = withAudio ? new[] { JsonSerializer.Deserialize<JsonElement>(sessionJson) } : Array.Empty<JsonElement>();
            var input = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, id = version.ToString("D"), createdAtMillis = 1,
                note = JsonSerializer.Deserialize<JsonElement>(noteJson), recordings
            });
            const string photoLabel = "\u7167\u7247";
            const string inkLabel = "\u624b\u5199\u5361\u7247";
            const string inkComment = "\u53ef\u7f16\u8f91\u7b14\u8ff9\u3001\u8eab\u4efd\u548c\u5c3a\u5bf8\u4fdd\u5b58\u5728\u540c\u5305 note.json \u4e2d";
            var markdown = $"{text}\n\n![{photoLabel}](assets/{photo:D}.{photoExtension})" +
                $"\n\n![{inkLabel}]({inkPath})\n<!-- {inkComment} -->";
            var content = new List<byte[]> { Encoding.UTF8.GetBytes(noteJson), Encoding.UTF8.GetBytes(markdown), photoContent, Png };
            var paths = new List<string> { "note.json", "document.md", $"assets/{photo:D}.{photoExtension}", inkPath };
            var kinds = new List<string> { "Document", "Markdown", "Photo", "InkPreview" };
            var blockIds = new List<string?> { null, null, photoBlock.ToString("D"), inkBlock.ToString("D") };
            var recordingIds = new List<string?> { null, null, null, null };
            var segments = new List<int?> { null, null, null, null };
            var chunkIndexes = new List<int?> { null, null, null, null };
            var samples = new List<long?> { null, null, null, null };
            if (withAudio)
            {
                var wav = new byte[44 + 160 * 2];
                "RIFF"u8.CopyTo(wav); BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(wav.Length - 8));
                "WAVEfmt "u8.CopyTo(wav.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
                BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);
                BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), 16_000); BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), 32_000);
                BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 2); BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
                "data"u8.CopyTo(wav.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), 320);
                content.Add(Encoding.UTF8.GetBytes(sessionJson)); content.Add(wav);
                paths.Add($"recordings/{recording:D}/manifest.json"); paths.Add($"recordings/{recording:D}/s0001-c000001.wav");
                kinds.Add("RecordingManifest"); kinds.Add("AudioChunk");
                blockIds.Add(null); blockIds.Add(null); recordingIds.Add(recording.ToString("D")); recordingIds.Add(recording.ToString("D"));
                segments.Add(null); segments.Add(1); chunkIndexes.Add(null); chunkIndexes.Add(1); samples.Add(null); samples.Add(160);
            }
            var files = paths.Select((path, i) => new
            {
                path, kind = kinds[i], sha256 = Digest(content[i]), sizeBytes = content[i].Length,
                blockId = blockIds[i], recordingId = recordingIds[i], segment = segments[i],
                chunkIndex = chunkIndexes[i], samples = samples[i]
            }).ToArray();
            var manifest = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, versionId = version.ToString("D"), noteId = note.ToString("D"), revision = 1,
                inputSha256 = Digest(Encoding.UTF8.GetBytes(input)), files
            });
            return new(new(version, note, null, input, manifest, true), content, 2, 3);
        }
    }
}
