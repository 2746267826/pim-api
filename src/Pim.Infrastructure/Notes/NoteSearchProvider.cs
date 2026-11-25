using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Modules;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Notes;

public sealed class NoteSearchProvider(
    PimDbContext db,
    ICurrentUserService currentUser,
    NoteMaterialUploadStore materials,
    NoteDerivedStore derived,
    NoteAsrTaskStore asrTasks,
    ILogger<NoteSearchProvider> logger) : ISearchProvider
{
    private static readonly Regex ImagePattern = new(@"!\[[^\]]*\]\([^)]*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LinkPattern = new(@"\[([^\]]*)\]\([^)]*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MarkupPattern = new(@"[#*_>`~]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WhitespacePattern = new(@"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Domain => "note";

    public async Task<SearchProviderResponse> SearchAsync(SearchProviderQuery query, CancellationToken ct)
    {
        var owner = currentUser.UserId;
        if (owner is null || query.Terms.Count == 0)
            return new SearchProviderResponse(Domain, 0, Array.Empty<SearchResult>());

        var sources = await materials.ListSearchSourcesAsync(owner.Value, ct);
        var snapshots = await derived.ListSearchSnapshotsAsync(owner.Value, ct);
        var transcripts = await LoadTranscriptsAsync(owner.Value, sources, ct);
        var matches = new List<SearchResult>();

        foreach (var source in sources)
        {
            var fields = ReadFields(source);
            if (snapshots.TryGetValue(source.NoteId, out var snapshot))
            {
                AddField(fields, "record", "完整记录", snapshot.CompleteRecord, 48);
                AddField(fields, "analysis", "独立分析", snapshot.IndependentAnalysis, 45);
            }
            if (transcripts.TryGetValue(source.NoteId, out var noteTranscripts))
            {
                foreach (var transcript in noteTranscripts)
                    AddField(fields, "transcript", "转写", transcript.Text, 42, transcript.Index);
            }

            var match = Match(fields, query);
            if (!match.Matches) continue;

            var title = fields.First(field => field.Name == "title").Text;
            var updatedAt = Max(source.CreatedAt,
                snapshot?.UpdatedAt ?? DateTimeOffset.MinValue);
            var createdAt = ReadCreatedAt(source.InputJson) ?? source.CreatedAt;
            matches.Add(new SearchResult(
                "notes",
                "note",
                source.NoteId.ToString("D"),
                title,
                Snippet(fields, query.Terms),
                $"/native-notes?note={source.NoteId:D}",
                Domain: Domain,
                Score: match.Score,
                MatchedFields: match.MatchedFields,
                FieldLocations: match.FieldLocations,
                SourceState: "server",
                UpdatedAt: updatedAt,
                CreatedAt: createdAt,
                SortKey: $"{Domain}|{source.NoteId:D}"));
        }

        return new SearchProviderResponse(Domain, matches.Count, matches);
    }

    private async Task<Dictionary<Guid, List<TranscriptField>>> LoadTranscriptsAsync(
        Guid owner, IReadOnlyList<NativeNoteSearchSource> sources, CancellationToken ct)
    {
        var result = new Dictionary<Guid, List<TranscriptField>>();
        var noteByVersion = sources.ToDictionary(source => source.VersionId, source => source.NoteId);
        var logOwners = new Dictionary<Guid, Guid>();
        foreach (var (_, taskId) in asrTasks.Enumerate(owner))
        {
            try
            {
                var state = await asrTasks.ReadAsync(owner, taskId, ct);
                if (!noteByVersion.TryGetValue(state.Request.VersionId, out var noteId)) continue;
                foreach (var step in state.Steps)
                {
                    if (step.Current.Status == "Succeeded" && step.Current.LogId is { } logId)
                        logOwners[logId] = noteId;
                }
            }
            catch (Exception error) when (error is IOException or JsonException
                or InvalidDataException or InvalidOperationException
                or Pim.Core.Exceptions.DomainException)
            {
                logger.LogWarning(error,
                    "Skipping an unreadable native-note ASR checkpoint during search");
            }
        }

        if (logOwners.Count > 0)
        {
            var logIds = logOwners.Keys.ToArray();
            var logs = await db.AiRequestLogs.AsNoTracking()
                .Where(log => log.UserId == owner && logIds.Contains(log.Id) &&
                    log.Status == "succeeded" && log.ResponseText != null)
                .Select(log => new { log.Id, log.ResponseText })
                .ToListAsync(ct);
            foreach (var log in logs)
            {
                if (string.IsNullOrWhiteSpace(log.ResponseText)) continue;
                AddTranscript(logOwners[log.Id], 0, log.ResponseText);
            }
        }

        var versionIds = noteByVersion.Keys.ToArray();
        if (versionIds.Length > 0)
        {
            var evidence = await db.NoteAsrSegmentEvidence.AsNoTracking()
                .Where(row => row.OwnerId == owner && versionIds.Contains(row.VersionId) &&
                    row.ResultText != "")
                .Select(row => new { row.VersionId, row.SegmentIndex, row.ResultText })
                .ToListAsync(ct);
            foreach (var row in evidence)
                AddTranscript(noteByVersion[row.VersionId], row.SegmentIndex + 1, row.ResultText);
        }

        return result;

        void AddTranscript(Guid noteId, int index, string text)
        {
            if (!result.TryGetValue(noteId, out var items))
            {
                items = [];
                result[noteId] = items;
            }
            if (items.Any(item => string.Equals(item.Text, text, StringComparison.Ordinal))) return;
            items.Add(new TranscriptField(index, text));
        }
    }

    private static List<SearchField> ReadFields(NativeNoteSearchSource source)
    {
        using var document = JsonDocument.Parse(source.InputJson);
        var note = document.RootElement.GetProperty("note");
        var title = note.TryGetProperty("title", out var titleNode) &&
                    titleNode.ValueKind == JsonValueKind.String
            ? titleNode.GetString() ?? "未命名笔记"
            : "未命名笔记";
        var fields = new List<SearchField>
        {
            new("title", "标题", title, 70)
        };
        if (note.TryGetProperty("blocks", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var block in blocks.EnumerateArray())
            {
                index++;
                if (!block.TryGetProperty("kind", out var kind) ||
                    kind.GetString() != "markdown" ||
                    !block.TryGetProperty("text", out var text) ||
                    text.ValueKind != JsonValueKind.String)
                    continue;
                AddField(fields, "body", "正文", text.GetString() ?? "", 58, index);
            }
        }
        AddOptionalText(note, fields, "body", "正文", 58);
        AddOptionalText(note, fields, "summary", "总结", 46);
        AddOptionalText(note, fields, "overview", "概览", 44);
        if (note.TryGetProperty("tags", out var tags))
        {
            var value = tags.ValueKind switch
            {
                JsonValueKind.Array => string.Join(' ', tags.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())),
                JsonValueKind.String => tags.GetString() ?? "",
                _ => ""
            };
            AddField(fields, "tags", "标签", value, 38);
        }
        return fields;
    }

    private static void AddOptionalText(JsonElement note, ICollection<SearchField> fields,
        string name, string label, double score)
    {
        if (note.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            AddField(fields, name, label, value.GetString() ?? "", score);
    }

    private static void AddField(ICollection<SearchField> fields, string name, string label,
        string text, double score, int? index = null)
    {
        if (!string.IsNullOrWhiteSpace(text))
            fields.Add(new SearchField(name, label, text, score, index));
    }

    private static MatchResult Match(IReadOnlyList<SearchField> fields, SearchProviderQuery query)
    {
        var matchedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedFields = new List<string>();
        var locations = new List<SearchFieldLocation>();
        var seenLabels = new HashSet<string>(StringComparer.Ordinal);
        double score = 0;
        foreach (var field in fields)
        {
            var hits = query.Terms
                .Where(term => field.Text.Contains(term, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (hits.Length == 0) continue;
            foreach (var term in hits)
            {
                matchedTerms.Add(term);
                score += field.Name == "title"
                    ? TitleScore(field.Text, term)
                    : field.Score;
            }
            if (!seenLabels.Add(field.Label)) continue;
            matchedFields.Add(field.Label);
            locations.Add(new SearchFieldLocation(field.Name, field.Label, field.Index));
        }

        var matches = query.Match == SearchMatchMode.All
            ? query.Terms.All(term => matchedTerms.Contains(term))
            : matchedTerms.Count > 0;
        return new MatchResult(matches, score, matchedFields, locations);
    }

    private static double TitleScore(string title, string term)
    {
        if (string.Equals(title.Trim(), term, StringComparison.OrdinalIgnoreCase)) return 120;
        if (title.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 90;
        return 70;
    }

    private static string Snippet(IReadOnlyList<SearchField> fields, IReadOnlyList<string> terms,
        int maxLength = 180)
    {
        var candidate = fields
            .Where(field => field.Name != "title")
            .FirstOrDefault(field => terms.Any(term =>
                field.Text.Contains(term, StringComparison.OrdinalIgnoreCase)));
        var plain = StripMarkdown(candidate?.Text ?? fields[0].Text);
        if (plain.Length == 0) return string.Empty;
        var positions = terms
            .Select(term => plain.IndexOf(term, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .ToArray();
        var first = positions.Length == 0 ? 0 : positions.Min();
        var start = Math.Max(0, first - maxLength / 4);
        var end = Math.Min(plain.Length, start + maxLength);
        if (end - start < maxLength && start > 0) start = Math.Max(0, end - maxLength);
        var text = plain[start..end].Trim();
        return (start > 0 ? "…" : string.Empty) + text + (end < plain.Length ? "…" : string.Empty);
    }

    private static string StripMarkdown(string value)
    {
        var withoutImages = ImagePattern.Replace(value, " ");
        var withoutLinks = LinkPattern.Replace(withoutImages, "$1");
        var withoutMarkup = MarkupPattern.Replace(withoutLinks, " ");
        return WhitespacePattern.Replace(withoutMarkup, " ").Trim();
    }

    private static DateTimeOffset? ReadCreatedAt(string inputJson)
    {
        try
        {
            using var document = JsonDocument.Parse(inputJson);
            if (!document.RootElement.TryGetProperty("note", out var note) ||
                !note.TryGetProperty("createdAtMillis", out var value) ||
                !value.TryGetInt64(out var millis) ||
                millis is < 0 or > 253402300799999)
                return null;
            return DateTimeOffset.FromUnixTimeMilliseconds(millis);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private sealed record SearchField(
        string Name,
        string Label,
        string Text,
        double Score,
        int? Index = null);

    private sealed record TranscriptField(int Index, string Text);

    private sealed record MatchResult(
        bool Matches,
        double Score,
        IReadOnlyList<string> MatchedFields,
        IReadOnlyList<SearchFieldLocation> FieldLocations);
}
