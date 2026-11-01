using System.Text;
using System.Text.Json;

namespace Pim.Infrastructure.Ai.Asr.Mimo;

public sealed record MimoBatchParseLimits(int MaxLineChars = 1024 * 1024, int MaxLines = 1_000_000,
    long MaxTotalChars = 64L * 1024 * 1024)
{
    public static readonly MimoBatchParseLimits Default = new();
}

/// <summary>
/// Pure, bounded JSONL reconciliation. Results are matched by the exact <c>custom_id</c>, never by
/// line order. HTTP 200 + non-empty text is not ASR success; it must pass the same canonical
/// audio-token and transcript checks as the synchronous parser.
/// </summary>
public static class MimoBatchResultParser
{
    public static MimoBatchReconciliation Reconcile(IReadOnlyCollection<string> expectedCustomIds,
        string? outputJsonl, string? errorJsonl = null, MimoBatchParseLimits? limits = null) =>
        Reconcile(expectedCustomIds,
            outputJsonl is null ? null : new StringReader(outputJsonl),
            errorJsonl is null ? null : new StringReader(errorJsonl), limits);

    public static MimoBatchReconciliation Reconcile(IReadOnlyCollection<string> expectedCustomIds,
        Stream? outputJsonl, Stream? errorJsonl = null, MimoBatchParseLimits? limits = null) =>
        Reconcile(expectedCustomIds, ToReader(outputJsonl), ToReader(errorJsonl), limits);

    /// <summary>Streaming entry point. The caller owns and disposes the readers.</summary>
    public static MimoBatchReconciliation Reconcile(IReadOnlyCollection<string> expectedCustomIds,
        TextReader? outputJsonl, TextReader? errorJsonl, MimoBatchParseLimits? limits = null)
    {
        limits ??= MimoBatchParseLimits.Default;
        if (limits.MaxLineChars <= 0 || limits.MaxLines <= 0 || limits.MaxTotalChars <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "Batch parse limits must be positive");
        if (expectedCustomIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("expected custom_id must be non-empty", nameof(expectedCustomIds));
        if (expectedCustomIds.Distinct(StringComparer.Ordinal).Count() != expectedCustomIds.Count)
            throw new ArgumentException("expected custom_id values must be unique", nameof(expectedCustomIds));

        var parsed = new Dictionary<string, List<ParsedLine>>(StringComparer.Ordinal);
        var order = new List<string>();
        var malformed = 0;
        var lineCount = 0;
        var budget = new CharBudget(limits.MaxTotalChars);

        void Read(TextReader? reader)
        {
            if (reader is null) return;
            var lines = new BoundedLineReader(reader, limits, budget);
            while (true)
            {
                var line = lines.ReadLine(out var overflow);
                if (line is null) break;
                if (++lineCount > limits.MaxLines)
                    throw new MimoBatchException(42201, "Batch结果JSONL超过行数上限，未解析");
                if (line.Length == 0 && !overflow) continue;
                if (overflow)
                {
                    malformed++;
                    continue;
                }
                var item = ParseLine(line);
                if (item is null)
                {
                    malformed++;
                    continue;
                }
                if (!parsed.TryGetValue(item.CustomId, out var bucket))
                {
                    bucket = [];
                    parsed[item.CustomId] = bucket;
                    order.Add(item.CustomId);
                }
                bucket.Add(item);
            }
        }

        Read(outputJsonl);
        Read(errorJsonl);

        var expected = expectedCustomIds.ToHashSet(StringComparer.Ordinal);
        var items = new List<MimoBatchItemOutcome>();
        var missing = new List<string>();
        var duplicates = new List<string>();
        var unknown = new List<string>();

        foreach (var customId in expectedCustomIds)
        {
            if (!parsed.TryGetValue(customId, out var bucket) || bucket.Count == 0)
            {
                missing.Add(customId);
                items.Add(new(customId, MimoBatchItemDisposition.Missing, null, null, null,
                    "missing", "结果文件中没有该custom_id", false, null, 0));
                continue;
            }
            var first = bucket[0];
            if (bucket.Count > 1)
            {
                duplicates.Add(customId);
                items.Add(new(customId, MimoBatchItemDisposition.Duplicate, first.StatusCode, first.Content,
                    first.RawBody, "duplicate", $"同一custom_id出现{bucket.Count}次", false, null, bucket.Count));
                continue;
            }
            items.Add(new(customId, first.Disposition, first.StatusCode, first.Content, first.RawBody,
                first.ErrorCode, first.ErrorMessage, first.AsrValidated, first.AsrErrorCode, 1));
        }

        foreach (var customId in order)
        {
            if (expected.Contains(customId)) continue;
            var bucket = parsed[customId];
            unknown.Add(customId);
            items.Add(new(customId, MimoBatchItemDisposition.UnknownCustomId, bucket[0].StatusCode,
                bucket[0].Content, bucket[0].RawBody, "unknown_custom_id",
                "结果包含输入批次中不存在的custom_id", false, null, bucket.Count));
        }

        return new(items, missing, duplicates, unknown, malformed);
    }

    private static TextReader? ToReader(Stream? stream) => stream is null
        ? null
        : new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096,
            leaveOpen: true);

    private static ParsedLine? ParseLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("custom_id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
                return null;
            var customId = idElement.GetString();
            // Never trim/normalize provider identifiers; they are opaque matching keys.
            if (string.IsNullOrWhiteSpace(customId)) return null;

            if (root.TryGetProperty("error", out var error) &&
                error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c)
                    ? c.ToString() : "error";
                var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m)
                    ? m.ToString() : error.ToString();
                return new(customId, MimoBatchItemDisposition.Failed, null, null, null,
                    code, message, false, null);
            }

            if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
                return new(customId, MimoBatchItemDisposition.Failed, null, null, null,
                    "missing_response", "结果行没有有效的response对象", false, null);

            int? status = null;
            if (response.TryGetProperty("status_code", out var statusCode) &&
                statusCode.ValueKind == JsonValueKind.Number && statusCode.TryGetInt32(out var parsedStatus))
                status = parsedStatus;
            if (status is not 200)
                return new(customId, MimoBatchItemDisposition.NonSuccessStatus, status, null, null,
                    "upstream_http_" + (status?.ToString() ?? "missing"), "上游请求未返回HTTP 200", false, null);

            if (!response.TryGetProperty("body", out var body) ||
                body.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return new(customId, MimoBatchItemDisposition.Empty, status, null, null,
                    "empty_content", "结果缺少响应body", false, "empty");

            var rawBody = body.GetRawText();
            if (body.ValueKind != JsonValueKind.Object)
                return new(customId, MimoBatchItemDisposition.AsrInvalid, status, null, rawBody,
                    "body_shape_invalid", "Batch body不是JSON对象", false, "body_shape_invalid");

            var semantics = MimoMultimodalResponseParser.Inspect(body);
            return semantics.Valid
                ? new(customId, MimoBatchItemDisposition.AsrSucceeded, status, semantics.Text, rawBody,
                    null, null, true, null)
                : new(customId, MimoBatchItemDisposition.AsrInvalid, status,
                    string.IsNullOrWhiteSpace(semantics.Text) ? null : semantics.Text, rawBody,
                    "asr_" + semantics.ErrorCode, "HTTP 200但未通过ASR输出验证", false, semantics.ErrorCode);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private sealed record ParsedLine(string CustomId, MimoBatchItemDisposition Disposition,
        int? StatusCode, string? Content, string? RawBody, string? ErrorCode, string? ErrorMessage,
        bool AsrValidated, string? AsrErrorCode);

    /// <summary>Reads one line at a time with hard char caps, without splitting/allocating the whole file.</summary>
    private sealed class CharBudget(long max)
    {
        private long _used;

        public void Consume()
        {
            if (++_used > max)
                throw new MimoBatchException(42202, "Batch结果JSONL超过总字符预算，未解析");
        }
    }

    private sealed class BoundedLineReader(TextReader reader, MimoBatchParseLimits limits, CharBudget budget)
    {
        private readonly char[] _buffer = new char[4096];
        private int _position;
        private int _length;

        public string? ReadLine(out bool overflow)
        {
            overflow = false;
            var builder = new StringBuilder(Math.Min(limits.MaxLineChars, 4096));
            var sawAny = false;
            while (true)
            {
                if (_position >= _length)
                {
                    _length = reader.Read(_buffer, 0, _buffer.Length);
                    _position = 0;
                    if (_length <= 0) break;
                }
                var ch = _buffer[_position++];
                sawAny = true;
                budget.Consume();
                if (ch == '\n') return builder.ToString();
                if (ch == '\r') continue;
                if (builder.Length < limits.MaxLineChars) builder.Append(ch);
                else overflow = true;
            }
            return sawAny ? builder.ToString() : null;
        }
    }
}
