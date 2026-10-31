using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Ai.Asr.Mimo;

namespace Pim.Infrastructure.Notes;

internal sealed class NoteAsrBatchJsonl(Stream content, long length, string[] customIds) : IAsyncDisposable
{
    public Stream Content { get; } = content;
    public long Length { get; } = length;
    public string[] CustomIds { get; } = customIds;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>
/// Bounded Batch JSONL spool. It never buffers the whole upload in managed memory and only accepts
/// the fixed MiMo multimodal request shape for the exact immutable attempt segment ids.
/// </summary>
internal static class NoteAsrBatchJsonlReader
{
    private const int MaxLineBytes = 72 * 1024 * 1024;
    private const int MaxPromptChars = 64 * 1024;
    private const int MaxCompletionTokens = 8192;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> Languages = new(["auto", "zh", "en"], StringComparer.Ordinal);

    internal static string CustomId(int segmentIndex) => $"seg-{segmentIndex}";

    /// <summary>A submission id is only acceptable when it names one real segment of the frozen plan.</summary>
    private static bool IsCanonicalIdForPlan(string id, IReadOnlyList<NoteAsrStep> steps)
    {
        var index = SegmentIndex(id);
        return index >= 0 && index < steps.Count && steps[index].Index == index;
    }

    /// <summary>
    /// Builds the exact provider JSONL from the server's trusted plan and immutable material. The
    /// caller supplies only a bounded subset of original segment ids; it never supplies audio.
    /// </summary>
    internal static async Task<NoteAsrBatchJsonl> GenerateFromPlanAsync(Guid owner,
        Guid versionId, IReadOnlyList<NoteAsrStep> steps, int maxSamplesPerCall, string language,
        string model, string prompt, NoteMaterialUploadStore materials, int maxBytes, string[] expectedIds,
        int[] indexes, CancellationToken ct)
    {
        if (steps.Count < 1 || indexes.Length < 1 || expectedIds.Length != steps.Count)
            throw new DomainException(40001, "Batch 服务端生成范围无效");
        var allowed = indexes.ToHashSet();
        if (allowed.Count != indexes.Length || allowed.Any(x => x < 0 || x >= steps.Count))
            throw new DomainException(40001, "Batch 服务端生成范围必须是不重复的原分片索引");
        var selected = steps.Where(x => allowed.Contains(x.Index)).ToArray();
        if (selected.Length != allowed.Count)
            throw new DomainException(40901, "Batch 服务端生成范围与固定分片不一致");
        var path = Path.Combine(Path.GetTempPath(), $"pim-batch-{Guid.NewGuid():N}.jsonl");
        var spool = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        long total = 0;
        try
        {
            using (var writer = new StreamWriter(spool, StrictUtf8, 81920, leaveOpen: true) { NewLine = "\n" })
            {
                foreach (var step in selected)
                {
                    if (step.Samples <= 0 || step.Samples > maxSamplesPerCall)
                        throw new DomainException(40901, "Batch 固定分片超出可信单片预算");
                    var audio = await materials.ReadAudioSliceAsync(owner, versionId, step.FileIndex,
                        step.SourceOffset, step.Samples, ct);
                    if (audio.Length < 45 || audio.Length > MimoAudioFormats.MaxDecodedBytes)
                        throw new DomainException(41301, "Batch 服务端音频分片超过50MiB");
                    var body = CanonicalLine(expectedIds[step.Index], audio, model, prompt);
                    await writer.WriteAsync(body.AsMemory(), ct);
                    await writer.WriteAsync('\n');
                    total = checked(total + StrictUtf8.GetByteCount(body) + 1);
                    if (total > maxBytes) throw new DomainException(41301, "Batch 服务端生成输入超过总预算，已拒绝且不会自动拆批或发起付费请求");
                }
                await writer.FlushAsync(ct);
            }
            total = spool.Length;
            if (total > maxBytes) throw new DomainException(41301, "Batch 服务端生成输入超过总预算，已拒绝且不会自动拆批或发起付费请求");
            spool.Position = 0;
            return new(spool, total, selected.Select(x => expectedIds[x.Index]).ToArray());
        }
        catch
        {
            await spool.DisposeAsync();
            throw;
        }
    }

    internal static async Task<NoteAsrBatchJsonl> ReadAsync(Stream source, long declaredLength,
        Guid owner, Guid versionId, IReadOnlyList<NoteAsrStep> steps, int maxSamplesPerCall, string language, string expectedModel,
        string expectedPrompt, NoteMaterialUploadStore materials, int maxBytes, string[] expectedIds,
        CancellationToken ct)
    {
        if (steps.Count is < 1 or > 5_000 || maxBytes <= 0 || maxSamplesPerCall <= 0 ||
            expectedIds.Length is < 1 || expectedIds.Length > steps.Count ||
            expectedIds.Distinct(StringComparer.Ordinal).Count() != expectedIds.Length ||
            expectedIds.Any(id => !IsCanonicalIdForPlan(id, steps)) ||
            string.IsNullOrWhiteSpace(expectedModel) ||
            !Languages.Contains(language) || string.IsNullOrWhiteSpace(expectedPrompt) ||
            expectedPrompt.Length > MaxPromptChars)
            throw new DomainException(40001, "Batch 输入预算、模型或分片数量无效");
        if (declaredLength <= 0 || declaredLength > maxBytes)
            throw new DomainException(41301, "Batch 输入必须非空且不超过配置与供应商共同上限");
        for (var index = 0; index < steps.Count; index++)
        {
            if (steps[index].Index != index || steps[index].Samples <= 0 || steps[index].Samples > maxSamplesPerCall)
                throw new DomainException(40901, "Batch 固定分片计划不连续，拒绝上传");
        }

        var path = Path.Combine(Path.GetTempPath(), $"pim-batch-{Guid.NewGuid():N}.jsonl");
        var spool = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        try
        {
            var chunk = new byte[81920];
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(chunk, ct);
                if (read == 0) break;
                total += read;
                if (total > maxBytes)
                    throw new DomainException(41301, "Batch 输入超过配置上限，未保存或上传");
                await spool.WriteAsync(chunk.AsMemory(0, read), ct);
            }
            if (total == 0 || total != declaredLength)
                throw new DomainException(40001, "Batch 输入长度与声明不一致，未上传");
            await spool.FlushAsync(ct);
            spool.Position = 0;

            var expected = expectedIds
                .ToHashSet(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ids = new List<string>(steps.Count);
            var lines = new BoundedLineReader(spool);
            var lineNumber = 0;
            while (await lines.ReadLineAsync(ct) is { } line)
            {
                lineNumber++;
                if (line.Length == 0) continue;
                string json;
                try
                {
                    json = StrictUtf8.GetString(line);
                }
                catch (DecoderFallbackException)
                {
                    throw InvalidLine(lineNumber);
                }

                ParsedLine parsed;
                try
                {
                    using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
                    if (!TryParseEnvelope(document.RootElement, expectedModel, expectedPrompt, out parsed))
                        throw InvalidLine(lineNumber);
                }
                catch (JsonException)
                {
                    throw InvalidLine(lineNumber);
                }

                if (!expected.Any(x => x == parsed.CustomId))
                    throw new DomainException(40301, "Batch custom_id 不能超出当前账号的 attempt 分片范围");
                if (!seen.Add(parsed.CustomId))
                    throw new DomainException(40001, "Batch JSONL 的 custom_id 必须唯一");
                var segmentIndex = SegmentIndex(parsed.CustomId);
                if (segmentIndex < 0 || segmentIndex >= steps.Count)
                    throw new DomainException(40301, "Batch custom_id 不能超出当前账号的 attempt 分片范围");
                var step = steps[segmentIndex];
                var trusted = await materials.ReadAudioSliceAsync(owner, versionId, step.FileIndex,
                    step.SourceOffset, step.Samples, ct);
                var uploaded = DecodeAudioDataUrl(parsed.DataUrl, parsed.Format, lineNumber);
                if (!ValidWav(uploaded) || uploaded.Length != trusted.Length ||
                    !CryptographicOperations.FixedTimeEquals(SHA256.HashData(uploaded), SHA256.HashData(trusted)) ||
                    !CryptographicOperations.FixedTimeEquals(uploaded, trusted))
                    throw new DomainException(40301,
                        $"Batch JSONL 第 {lineNumber} 行音频与固定材料、分片范围或格式不一致，未上传");
                ids.Add(parsed.CustomId);
            }

            if (ids.Count == 0 || !ids.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
                throw new DomainException(40901, "Batch 输入未完整覆盖本次可信提交范围");

            spool.Position = 0;
            return new NoteAsrBatchJsonl(spool, total, ids.ToArray());
        }
        catch
        {
            await spool.DisposeAsync();
            throw;
        }
    }

    private static bool TryParseEnvelope(JsonElement root, string expectedModel, string expectedPrompt,
        out ParsedLine parsed)
    {
        parsed = default;
        if (root.ValueKind != JsonValueKind.Object ||
            !OnlyProperties(root, "custom_id", "method", "url", "body") ||
            !root.TryGetProperty("custom_id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(idElement.GetString()) ||
            idElement.GetString() is not { } customId || customId.Length > 200 || customId.Any(char.IsControl) ||
            !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String ||
            method.GetString() != "POST" ||
            !root.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String ||
            url.GetString() != MimoBatchDefaults.ChatCompletionsEndpoint ||
            !root.TryGetProperty("body", out var body) ||
            !TryParseBody(body, expectedModel, expectedPrompt, out var dataUrl, out var format))
            return false;
        parsed = new ParsedLine(customId, dataUrl, format);
        return true;
    }

    /// <summary>One canonical provider request shared by server generation and the upload validator.</summary>
    private static string CanonicalLine(string customId, byte[] audio, string model, string prompt) =>
        JsonSerializer.Serialize(new
        {
            custom_id = customId,
            method = "POST",
            url = MimoBatchDefaults.ChatCompletionsEndpoint,
            body = CanonicalBody(audio, model, prompt)
        });

    private static object CanonicalBody(byte[] audio, string model, string prompt) => new
    {
        model,
        messages = new object[]
        {
            new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "input_audio",
                        input_audio = new { data = MimoAudioFormats.DataUrl("wav", audio), format = "wav" } },
                    new { type = "text", text = prompt }
                }
            }
        },
        max_completion_tokens = MaxCompletionTokens,
        stream = false
    };

    private static bool TryParseBody(JsonElement body, string expectedModel, string expectedPrompt,
        out string dataUrl, out string format)
    {
        dataUrl = "";
        format = "";
        if (body.ValueKind != JsonValueKind.Object ||
            !OnlyProperties(body, "model", "messages", "max_completion_tokens", "stream") ||
            !body.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String ||
            !string.Equals(model.GetString(), expectedModel, StringComparison.Ordinal) ||
            body.TryGetProperty("max_completion_tokens", out var maxTokens) &&
            (maxTokens.ValueKind != JsonValueKind.Number || !maxTokens.TryGetInt32(out var tokenLimit) ||
             tokenLimit != MaxCompletionTokens) ||
            !body.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.False ||
            !body.TryGetProperty("messages", out var messages) ||
            messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() != 1)
            return false;

        var message = messages[0];
        if (message.ValueKind != JsonValueKind.Object ||
            !OnlyProperties(message, "role", "content") ||
            !message.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String ||
            role.GetString() != "user" ||
            !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array ||
            content.GetArrayLength() != 2)
            return false;

        var audioPart = content[0];
        var textPart = content[1];
        if (audioPart.ValueKind != JsonValueKind.Object ||
            !OnlyProperties(audioPart, "type", "input_audio") ||
            !audioPart.TryGetProperty("type", out var audioType) || audioType.ValueKind != JsonValueKind.String ||
            audioType.GetString() != "input_audio" ||
            !audioPart.TryGetProperty("input_audio", out var audio) ||
            audio.ValueKind != JsonValueKind.Object ||
            !OnlyProperties(audio, "data", "format") ||
            !audio.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String ||
            data.GetString() is not { } audioData ||
            !audio.TryGetProperty("format", out var formatElement) ||
            formatElement.ValueKind != JsonValueKind.String ||
            formatElement.GetString() is not { } audioFormat || audioFormat != "wav")
            return false;
        if (textPart.ValueKind != JsonValueKind.Object ||
            !OnlyProperties(textPart, "type", "text") ||
            !textPart.TryGetProperty("type", out var textType) || textType.ValueKind != JsonValueKind.String ||
            textType.GetString() != "text" ||
            !textPart.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String ||
            text.GetString() is not { } prompt || prompt.Length > MaxPromptChars ||
            !string.Equals(prompt, expectedPrompt, StringComparison.Ordinal))
            return false;
        dataUrl = audioData;
        format = audioFormat;
        return true;
    }

    private static byte[] DecodeAudioDataUrl(string value, string format, int lineNumber)
    {
        const string prefix = "data:audio/wav;base64,";
        if (format != "wav" || !value.StartsWith(prefix, StringComparison.Ordinal))
            throw new DomainException(40301, $"Batch JSONL 第 {lineNumber} 行音频格式必须为 WAV");
        var payload = value.AsSpan(prefix.Length);
        const int maxBase64Chars = ((MimoAudioFormats.MaxDecodedBytes + 2) / 3) * 4;
        if (payload.Length == 0 || payload.Length % 4 != 0 || payload.Length > maxBase64Chars)
            throw new DomainException(41301, $"Batch JSONL 第 {lineNumber} 行音频超过50MiB或不是有效base64");
        foreach (var valueChar in payload)
        {
            if (!(valueChar is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '='))
                throw new DomainException(40001, $"Batch JSONL 第 {lineNumber} 行音频不是有效base64");
        }
        var decoded = new byte[checked(payload.Length / 4 * 3)];
        if (!Convert.TryFromBase64Chars(payload, decoded, out var written) || written <= 0)
            throw new DomainException(40001, $"Batch JSONL 第 {lineNumber} 行音频不是有效base64");
        if (written != decoded.Length) Array.Resize(ref decoded, written);
        return decoded;
    }

    private static bool ValidWav(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 44 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 8).SequenceEqual("WAVEfmt "u8) &&
        bytes.Slice(36, 4).SequenceEqual("data"u8);

    private static int SegmentIndex(string customId)
    {
        const string prefix = "seg-";
        return customId.StartsWith(prefix, StringComparison.Ordinal) &&
               int.TryParse(customId.AsSpan(prefix.Length), System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out var index) &&
               index >= 0 && customId == CustomId(index)
            ? index
            : -1;
    }

    private static bool OnlyProperties(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) return false;
        return true;
    }

    private readonly record struct ParsedLine(string CustomId, string DataUrl, string Format);

    private static DomainException InvalidLine(int oneBasedLine) =>
        new(40001, $"Batch JSONL 第 {oneBasedLine} 行结构无效；必须绑定固定 MiMo 多模态音频请求");

    private sealed class BoundedLineReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[81920];
        private int _offset;
        private int _count;

        public async ValueTask<byte[]?> ReadLineAsync(CancellationToken ct)
        {
            var line = new ArrayBufferWriter<byte>();
            while (true)
            {
                if (_offset >= _count)
                {
                    _count = await stream.ReadAsync(_buffer, ct);
                    _offset = 0;
                    if (_count == 0)
                        return line.WrittenCount == 0 ? null : Finish(line);
                }

                var newline = Array.IndexOf(_buffer, (byte)'\n', _offset, _count - _offset);
                var take = newline >= 0 ? newline - _offset : _count - _offset;
                if (line.WrittenCount + take > MaxLineBytes)
                    throw new DomainException(41301, "Batch JSONL 单行超过 72MiB，未上传");
                Write(line, _buffer, _offset, take);
                _offset += take;
                if (newline >= 0)
                {
                    _offset++;
                    return Finish(line);
                }
            }
        }

        private static byte[] Finish(ArrayBufferWriter<byte> line)
        {
            var bytes = line.WrittenSpan;
            return bytes.Length > 0 && bytes[^1] == (byte)'\r'
                ? bytes[..^1].ToArray()
                : bytes.ToArray();
        }

        private static void Write(ArrayBufferWriter<byte> line, byte[] source, int offset, int count) =>
            line.Write(source.AsSpan(offset, count));
    }
}
