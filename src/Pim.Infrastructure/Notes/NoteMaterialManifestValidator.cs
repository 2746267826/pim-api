using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pim.Core.Exceptions;
using Pim.Core.Notes;

namespace Pim.Infrastructure.Notes;

internal sealed record NoteUploadedMaterial(
    string Path, string Kind, string Sha256, long SizeBytes,
    string? BlockId, string? RecordingId, int? Segment, int? ChunkIndex, long? Samples);

internal static partial class NoteMaterialManifestValidator
{
    internal static IReadOnlyList<NoteUploadedMaterial> Validate(
        BeginNoteMaterialUploadRequest request, NoteMaterialUploadOptions options)
    {
        try
        {
            Require(request.StorageConsent, "请确认将本次固定材料上传至当前服务器");
            Require(request.VersionId != Guid.Empty && request.NoteId != Guid.Empty && request.BaseVersionId != Guid.Empty,
                "笔记或材料版本标识无效");
            Require(request.InputJson is { Length: > 0 and <= NoteMaterialUploadOptions.MaxMetadataCharacters } &&
                request.ManifestJson is { Length: > 0 and <= NoteMaterialUploadOptions.MaxMetadataCharacters }, "材料清单过大或为空");
            using var inputDocument = JsonDocument.Parse(request.InputJson);
            using var manifestDocument = JsonDocument.Parse(request.ManifestJson);
            var input = inputDocument.RootElement;
            var manifest = manifestDocument.RootElement;
            UniqueProperties(input); UniqueProperties(manifest);
            Require(Int(input, "schemaVersion") == 1 && Int(manifest, "schemaVersion") == 1, "暂不支持该材料版本");
            Require(Long(input, "createdAtMillis") >= 0, "材料时间无效");
            Require(Id(input, "id") == request.VersionId && Id(manifest, "versionId") == request.VersionId &&
                Id(manifest, "noteId") == request.NoteId, "材料清单与提交标识不一致");
            Require(String(manifest, "inputSha256") == Hash(request.InputJson), "固定输入摘要不匹配");
            var note = input.GetProperty("note");
            Require(Int(note, "schemaVersion") == 1 && Id(note, "id") == request.NoteId &&
                Long(note, "revision") >= 0 && Long(note, "revision") == Long(manifest, "revision") &&
                String(note, "title").Length <= 500, "笔记原稿与材料版本不一致");
            Require(Long(note, "createdAtMillis") >= 0, "原稿时间无效");
            var expected = new Dictionary<string, ExpectedMaterial>(StringComparer.Ordinal)
            {
                ["note.json"] = new("Document", Hash(note.GetRawText()), SizeBytes: Encoding.UTF8.GetByteCount(note.GetRawText()))
            };
            var markdown = new List<string>();
            var ids = new HashSet<Guid>();
            var blocks = note.GetProperty("blocks");
            Require(blocks.GetArrayLength() is > 0 && blocks.GetArrayLength() <= options.MaxFiles, "正文块数量超过上限");
            foreach (var block in blocks.EnumerateArray())
            {
                var blockId = Id(block, "id");
                Require(ids.Add(blockId), "原稿包含重复的内容块");
                var kind = String(block, "kind");
                if (kind == "markdown") { markdown.Add(String(block, "text")); continue; }
                if (kind == "photo")
                {
                    var assetId = Id(block, "assetId");
                    var extension = String(block, "mimeType") switch
                    {
                        "image/jpeg" => "jpg", "image/png" => "png", "image/webp" => "webp",
                        _ => throw Invalid("照片格式不受支持")
                    };
                    var path = $"assets/{assetId:D}.{extension}";
                    Require(expected.TryAdd(path, new("Photo", String(block, "sha256"), BlockId: blockId.ToString("D"))),
                        "原稿包含重复附件路径");
                    markdown.Add($"![照片]({path})");
                    continue;
                }
                Require(kind == "ink", "未知内容块不能被视为完整材料，请更新客户端后重试");
                var ink = block.GetProperty("ink");
                ValidateInk(ink);
                var inkPath = $"assets/{blockId:D}-{Hash(ink.GetRawText())}.png";
                expected.Add(inkPath, new("InkPreview", BlockId: blockId.ToString("D")));
                markdown.Add($"![手写卡片]({inkPath})\n<!-- 可编辑笔迹、身份和尺寸保存在同包 note.json 中 -->");
            }
            var exportedMarkdown = string.Join("\n\n", markdown);
            expected["document.md"] = new("Markdown", Hash(exportedMarkdown), SizeBytes: Encoding.UTF8.GetByteCount(exportedMarkdown));
            var recordings = new HashSet<Guid>();
            foreach (var recording in input.GetProperty("recordings").EnumerateArray())
            {
                var recordingId = Id(recording, "id");
                var currentSegment = Int(recording, "currentSegment");
                Require(Long(recording, "createdAtMillis") >= 0, "录音时间无效");
                Require(recordings.Add(recordingId) && Id(recording, "noteId") == request.NoteId &&
                    Int(recording, "schemaVersion") == 1 && currentSegment > 0 &&
                    String(recording, "phase") is "Completed" or "Interrupted", "录音尚未结束或归属无效");
                var problem = OptionalString(recording, "problem");
                Require(problem is not ("Damaged" or "Storage" or "Empty"), "录音缺损，不能提交为完整材料");
                var prefix = $"recordings/{recordingId:D}";
                expected.Add($"{prefix}/manifest.json", new("RecordingManifest", Hash(recording.GetRawText()), RecordingId: recordingId.ToString("D"),
                    SizeBytes: Encoding.UTF8.GetByteCount(recording.GetRawText())));
                long samples = 0;
                foreach (var chunk in recording.GetProperty("chunks").EnumerateArray())
                {
                    var segment = Int(chunk, "segment"); var index = Int(chunk, "index"); var count = Long(chunk, "samples");
                    Require(segment > 0 && segment <= currentSegment && index > 0 && count is >= 0 and <= 172_800_000,
                        "录音分片信息无效");
                    var path = $"{prefix}/s{segment:D4}-c{index:D6}.wav";
                    Require(expected.TryAdd(path, new("AudioChunk", RecordingId: recordingId.ToString("D"),
                        Segment: segment, ChunkIndex: index, Samples: count)), "录音包含重复分片");
                    samples = checked(samples + count);
                }
                Require(samples > 0, "录音没有有效音频");
            }
            var files = manifest.GetProperty("files");
            Require(files.GetArrayLength() == expected.Count && expected.Count <= options.MaxFiles, "材料清单不完整或超过数量上限");
            var result = new List<NoteUploadedMaterial>();
            long total = 0;
            foreach (var file in files.EnumerateArray())
            {
                var path = String(file, "path");
                Require(expected.Remove(path, out var source), "材料清单包含缺失、重复或多余的文件");
                var hash = String(file, "sha256"); var size = Long(file, "sizeBytes");
                Require(IsDigest(hash) && size >= 0 && size <= options.MaxVersionBytes, "材料文件大小或摘要无效");
                Require(String(file, "kind") == source!.Kind && (source.Hash is null || source.Hash == hash) &&
                    (source.SizeBytes is null || source.SizeBytes == size) &&
                    OptionalString(file, "blockId") == source.BlockId && OptionalString(file, "recordingId") == source.RecordingId &&
                    OptionalInt(file, "segment") == source.Segment && OptionalInt(file, "chunkIndex") == source.ChunkIndex &&
                    OptionalLong(file, "samples") == source.Samples, "文件描述与固定原稿不一致");
                if (source.Samples is { } audioSamples) Require(size == checked(44 + audioSamples * 2), "录音 WAV 大小与样本数不一致");
                if (source.Kind == "Photo") Require(size is > 0 and <= 32 * 1024 * 1024, "照片大小无效或超过 32 MB");
                if (source.Kind == "InkPreview") Require(size is > 0 and <= 40 * 1024 * 1024, "手写预览图大小无效或超过 40 MB");
                total = checked(total + size);
                Require(total <= options.MaxVersionBytes, "本次材料超过服务器单版本容量上限");
                result.Add(new(path, source.Kind, hash, size, source.BlockId, source.RecordingId, source.Segment, source.ChunkIndex, source.Samples));
            }
            Require(expected.Count == 0, "缺少固定原稿所需的材料");
            return result;
        }
        catch (DomainException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or
            FormatException or OverflowException or ArgumentException)
        { throw Invalid("固定原稿或材料清单格式无效"); }
    }

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    internal static bool IsDigest(string text) => DigestPattern().IsMatch(text);
    private static string String(JsonElement node, string key) => node.GetProperty(key).GetString() ?? throw Invalid("材料属性不能为空");
    private static int Int(JsonElement node, string key) => node.GetProperty(key).GetInt32();
    private static long Long(JsonElement node, string key) => node.GetProperty(key).GetInt64();
    private static Guid Id(JsonElement node, string key)
    {
        var text = String(node, key);
        Require(Guid.TryParseExact(text, "D", out var value) && value != Guid.Empty && value.ToString("D") == text, "材料标识无效");
        return value;
    }
    private static string? OptionalString(JsonElement node, string key) => node.TryGetProperty(key, out var item) && item.ValueKind != JsonValueKind.Null ? item.GetString() : null;
    private static int? OptionalInt(JsonElement node, string key) => node.TryGetProperty(key, out var item) && item.ValueKind != JsonValueKind.Null ? item.GetInt32() : null;
    private static long? OptionalLong(JsonElement node, string key) => node.TryGetProperty(key, out var item) && item.ValueKind != JsonValueKind.Null ? item.GetInt64() : null;
    private static void UniqueProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            { Require(names.Add(property.Name), "JSON 包含重复属性"); UniqueProperties(property.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) UniqueProperties(item);
    }
    private static void ValidateInk(JsonElement ink)
    {
        var width = ink.GetProperty("width").GetDouble(); var height = ink.GetProperty("height").GetDouble();
        Require(double.IsFinite(width) && width is >= 80 and <= 2048 &&
            double.IsFinite(height) && height is >= 80 and <= 4096, "手写卡片尺寸无效");
        foreach (var stroke in ink.GetProperty("strokes").EnumerateArray())
        {
            var pen = stroke.GetProperty("width").GetDouble(); var points = stroke.GetProperty("points");
            Require(double.IsFinite(pen) && pen is >= 0.5 and <= 24 && points.GetArrayLength() > 0, "手写笔迹无效");
            foreach (var point in points.EnumerateArray())
            {
                var x = point.GetProperty("x").GetDouble(); var y = point.GetProperty("y").GetDouble(); var pressure = point.GetProperty("pressure").GetDouble();
                Require(double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(pressure) &&
                    x >= 0 && x <= width && y >= 0 && y <= height && pressure is >= 0 and <= 1, "手写笔迹超出原稿范围");
            }
        }
    }
    private static void Require(bool value, string message) { if (!value) throw Invalid(message); }
    private static DomainException Invalid(string message) => new(40001, message);
    private sealed record ExpectedMaterial(string Kind, string? Hash = null, string? BlockId = null,
        string? RecordingId = null, int? Segment = null, int? ChunkIndex = null, long? Samples = null, long? SizeBytes = null);
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)] private static partial Regex DigestPattern();
}
