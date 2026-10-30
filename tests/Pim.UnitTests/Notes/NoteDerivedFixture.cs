using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pim.Core.Notes;
using Pim.Infrastructure.Notes;

namespace Pim.UnitTests.Notes;

internal static class NoteDerivedFixture
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static readonly byte[] PhotoBytes = CreateRgbaPng(214, 73, 52, 255);
    internal static readonly byte[] InkBytes = CreateRgbaPng(32, 91, 176, 255);

    internal static Material CreateMaterial(bool withAudio = true, int audioSamples = 160)
    {
        if (PhotoBytes.SequenceEqual(InkBytes))
            throw new InvalidOperationException("Fixture Photo and InkPreview bytes must differ");
        var version = Guid.NewGuid();
        var note = Guid.NewGuid();
        var recording = Guid.NewGuid();
        var noteBlock = Guid.NewGuid();
        var photoBlock = Guid.NewGuid();
        var photoAsset = Guid.NewGuid();
        var inkBlock = Guid.NewGuid();
        var text = "## Complete markdown\n\nEvery source line must remain visible.";
        var ink = new { width = 320.0, height = 180.0, strokes = Array.Empty<object>() };
        var inkJson = JsonSerializer.Serialize(ink, Json);
        var inkPath = $"assets/{inkBlock:D}-{Digest(Encoding.UTF8.GetBytes(inkJson))}.png";
        var photoPath = $"assets/{photoAsset:D}.png";
        var noteJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id = note.ToString("D"),
            title = "Derived fixture",
            revision = 1,
            createdAtMillis = 1,
            blocks = new object[]
            {
                new { id = noteBlock.ToString("D"), kind = "markdown", text },
                new { id = photoBlock.ToString("D"), kind = "photo", assetId = photoAsset.ToString("D"),
                    mimeType = "image/png", sha256 = Digest(PhotoBytes) },
                new { id = inkBlock.ToString("D"), kind = "ink", ink }
            }
        }, Json);
        var sessionJson = JsonSerializer.Serialize(new
        {
            id = recording.ToString("D"),
            schemaVersion = 1,
            createdAtMillis = 1,
            noteId = note.ToString("D"),
            phase = "Completed",
            currentSegment = 1,
            problem = (string?)null,
            chunks = new[] { new { segment = 1, index = 1, samples = audioSamples } }
        }, Json);
        var input = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id = version.ToString("D"),
            createdAtMillis = 1,
            note = JsonSerializer.Deserialize<JsonElement>(noteJson, Json),
            recordings = withAudio
                ? new[] { JsonSerializer.Deserialize<JsonElement>(sessionJson, Json) }
                : []
        }, Json);
        var markdown = $"{text}\n\n![照片]({photoPath})\n\n![手写卡片]({inkPath})" +
            "\n<!-- 可编辑笔迹、身份和尺寸保存在同包 note.json 中 -->";
        var content = new List<byte[]>
        {
            Encoding.UTF8.GetBytes(noteJson),
            Encoding.UTF8.GetBytes(markdown),
            PhotoBytes.ToArray(),
            InkBytes.ToArray()
        };
        var paths = new List<string> { "note.json", "document.md", photoPath, inkPath };
        var kinds = new List<string> { "Document", "Markdown", "Photo", "InkPreview" };
        var blockIds = new List<string?> { null, null, photoBlock.ToString("D"), inkBlock.ToString("D") };
        var recordingIds = new List<string?> { null, null, null, null };
        var segments = new List<int?> { null, null, null, null };
        var chunkIndexes = new List<int?> { null, null, null, null };
        var samples = new List<long?> { null, null, null, null };
        if (withAudio)
        {
            var wave = new byte[44 + audioSamples * 2];
            "RIFF"u8.CopyTo(wave);
            BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), (uint)(wave.Length - 8));
            "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
            BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24), 16_000);
            BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28), 32_000);
            BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34), 16);
            "data"u8.CopyTo(wave.AsSpan(36));
            BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), (uint)(audioSamples * 2));
            content.Add(Encoding.UTF8.GetBytes(sessionJson));
            content.Add(wave);
            paths.Add($"recordings/{recording:D}/manifest.json");
            paths.Add($"recordings/{recording:D}/s0001-c000001.wav");
            kinds.Add("RecordingManifest");
            kinds.Add("AudioChunk");
            blockIds.Add(null);
            blockIds.Add(null);
            recordingIds.Add(recording.ToString("D"));
            recordingIds.Add(recording.ToString("D"));
            segments.Add(null);
            segments.Add(1);
            chunkIndexes.Add(null);
            chunkIndexes.Add(1);
            samples.Add(null);
            samples.Add(audioSamples);
        }
        var files = paths.Select((path, index) => new
        {
            path,
            kind = kinds[index],
            sha256 = Digest(content[index]),
            sizeBytes = content[index].Length,
            blockId = blockIds[index],
            recordingId = recordingIds[index],
            segment = segments[index],
            chunkIndex = chunkIndexes[index],
            samples = samples[index]
        }).ToArray();
        var manifest = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            versionId = version.ToString("D"),
            noteId = note.ToString("D"),
            revision = 1,
            inputSha256 = Digest(Encoding.UTF8.GetBytes(input)),
            files
        }, Json);
        return new(new(version, note, null, input, manifest, true), content, 2, 3,
            version, note, recording, markdown);
    }

    internal static async Task UploadAsync(NoteMaterialUploadStore store, Guid owner, Material material)
    {
        await store.BeginAsync(owner, material.Request);
        for (var file = 0; file < material.Content.Count; file++)
        {
            var bytes = material.Content[file];
            var parts = (bytes.Length + NoteMaterialUploadOptions.PartSizeBytes - 1) /
                NoteMaterialUploadOptions.PartSizeBytes;
            for (var part = 0; part < parts; part++)
            {
                var start = part * NoteMaterialUploadOptions.PartSizeBytes;
                var bytesInPart = Math.Min(NoteMaterialUploadOptions.PartSizeBytes, bytes.Length - start);
                var chunk = bytes.AsSpan(start, bytesInPart).ToArray();
                await store.UploadPartAsync(owner, material.Request.VersionId, file, part, Digest(chunk),
                    new MemoryStream(chunk));
            }
        }
        var completed = await store.CompleteAsync(owner, material.Request.VersionId);
        if (completed.Phase != "Uploaded") throw new InvalidOperationException("Fixture upload was not committed");
    }

    internal static NoteDerivedInputSnapshot Snapshot(Material material, IReadOnlyList<NoteDerivedImageInput>? images = null,
        IReadOnlyList<NoteDerivedTranscriptInput>? transcripts = null, IReadOnlyList<NoteDerivedIssueDto>? issues = null)
    {
        return new(Guid.NewGuid(), material.NoteId, material.Request.VersionId, null, null,
            Digest(Encoding.UTF8.GetBytes(material.Request.InputJson)),
            Digest(Encoding.UTF8.GetBytes(material.Request.ManifestJson)),
            "Derived fixture", material.Markdown,
            [new(0, Guid.NewGuid(), material.Markdown)], transcripts ?? [], images ?? [], issues ?? []);
    }

    internal static NoteDerivedImageInput Image(int ordinal, int fileIndex, string kind, string sha256,
        long sizeBytes, string? path = null)
    {
        var extension = ".png";
        return new(ordinal, fileIndex, path ?? $"assets/{ordinal:D4}-{kind.ToLowerInvariant()}{extension}",
            kind, sha256, sizeBytes, "image/png", Guid.NewGuid().ToString("D"));
    }

    internal static string Digest(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] CreateRgbaPng(byte red, byte green, byte blue, byte alpha)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR"u8, header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                zlib.Write([0, red, green, blue, alpha]);
            WriteChunk(output, "IDAT"u8, compressed.ToArray());
        }
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();

        static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            output.Write(length);
            output.Write(type);
            output.Write(data);
            var crcInput = new byte[type.Length + data.Length];
            type.CopyTo(crcInput);
            data.CopyTo(crcInput.AsSpan(type.Length));
            Span<byte> crc = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
            output.Write(crc);
        }

        static uint Crc32(ReadOnlySpan<byte> bytes)
        {
            var crc = 0xffffffffu;
            foreach (var value in bytes)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc & 1) == 0 ? crc >> 1 : 0xedb88320u ^ crc >> 1;
            }
            return ~crc;
        }
    }

    internal sealed record Material(
        BeginNoteMaterialUploadRequest Request,
        List<byte[]> Content,
        int PhotoIndex,
        int InkIndex,
        Guid VersionId,
        Guid NoteId,
        Guid RecordingId,
        string Markdown);
}
