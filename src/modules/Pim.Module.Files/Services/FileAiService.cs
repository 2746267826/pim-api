using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Files.DTOs;
using Pim.Module.Files.Entities;

namespace Pim.Module.Files.Services;

public sealed class FileAiService(
    PimDbContext db,
    ICurrentUserService currentUser,
    IAiGateway aiGateway)
{
    public const string SummarySchemaName = "files.summary.v1";
    public const string SuggestionsSchemaName = "files.organization_suggestions.v1";
    private const string SchemaVersion = "1";

    private Guid UserId => currentUser.UserId ?? throw new DomainException(1002, "Not authenticated");

    public async Task<FileAiResultDto?> GenerateSummaryAndTagsAsync(
        Guid fileItemId,
        CancellationToken ct = default)
    {
        var context = await LoadContextAsync(fileItemId, ct);
        var request = BuildGatewayRequest(
            context,
            "file.summary",
            SummarySchemaName,
            BuildSummaryPrompt(context));
        var result = await aiGateway.SendAsync(request, ct);
        if (!IsSuccessful(result))
            return null;

        var output = result.ParsedOutput?.RootElement
            ?? JsonDocument.Parse(result.ResponseText ?? "{}").RootElement;
        var tags = output.TryGetProperty("tags", out var tagsElement)
            ? tagsElement.EnumerateArray()
                .Select(tag => tag.GetString())
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag!)
                .ToList()
            : [];
        var evidenceChunkIds = context.Chunks.Select(chunk => chunk.Id).ToList();
        var now = DateTimeOffset.UtcNow;

        var existing = await db.Set<FileAiResultEntity>()
            .FirstOrDefaultAsync(ai =>
                ai.FileItemId == context.Item.Id
                && ai.VersionId == context.Version.Id,
                ct);
        if (existing is null)
        {
            existing = new FileAiResultEntity
            {
                FileItemId = context.Item.Id,
                VersionId = context.Version.Id
            };
            db.Set<FileAiResultEntity>().Add(existing);
        }

        existing.Summary = ReadString(output, "summary") ?? string.Empty;
        existing.TagsJson = JsonSerializer.Serialize(tags);
        existing.Language = ReadString(output, "language");
        existing.Sensitivity = ReadString(output, "sensitivity");
        existing.GeneratedAt = now;
        existing.Model = result.Model;
        existing.AiRequestLogId = result.LogId;
        existing.EvidenceChunkIdsJson = JsonSerializer.Serialize(evidenceChunkIds);

        await db.SaveChangesAsync(ct);
        return MapAiResult(existing);
    }

    public async Task<IReadOnlyList<FileSuggestionDto>> GenerateOrganizationSuggestionsAsync(
        Guid fileItemId,
        CancellationToken ct = default)
    {
        var context = await LoadContextAsync(fileItemId, ct);
        var request = BuildGatewayRequest(
            context,
            "file.organization_suggestions",
            SuggestionsSchemaName,
            BuildSuggestionsPrompt(context));
        var result = await aiGateway.SendAsync(request, ct);
        if (!IsSuccessful(result))
            return [];

        var output = result.ParsedOutput?.RootElement
            ?? JsonDocument.Parse(result.ResponseText ?? "{}").RootElement;
        if (!output.TryGetProperty("suggestions", out var suggestionsElement)
            || suggestionsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var suggestions = new List<FileSuggestionEntity>();
        foreach (var suggestionElement in suggestionsElement.EnumerateArray())
        {
            var suggestionType = ReadString(suggestionElement, "suggestionType");
            var title = ReadString(suggestionElement, "title");
            var reason = ReadString(suggestionElement, "reason");
            if (string.IsNullOrWhiteSpace(suggestionType)
                || string.IsNullOrWhiteSpace(title)
                || string.IsNullOrWhiteSpace(reason))
            {
                continue;
            }

            var confidence = suggestionElement.TryGetProperty("confidence", out var confidenceElement)
                && confidenceElement.TryGetDecimal(out var parsedConfidence)
                ? Math.Clamp(parsedConfidence, 0m, 1m)
                : 0m;
            var payloadJson = suggestionElement.TryGetProperty("payload", out var payloadElement)
                ? payloadElement.GetRawText()
                : "{}";
            var entity = new FileSuggestionEntity
            {
                FileItemId = context.Item.Id,
                SuggestionType = suggestionType,
                Title = title,
                Reason = reason,
                Confidence = confidence,
                PayloadJson = payloadJson,
                Status = "pending",
                AiRequestLogId = result.LogId,
                CreatedAt = now,
                UpdatedAt = now
            };
            suggestions.Add(entity);
        }

        db.Set<FileSuggestionEntity>().AddRange(suggestions);
        await db.SaveChangesAsync(ct);

        return suggestions.Select(MapSuggestion).ToList();
    }

    public static void RegisterSchemas(IAiSchemaRegistry registry)
    {
        registry.Register(new AiSchemaDefinition(
            SummarySchemaName,
            SchemaVersion,
            """
            {
              "type": "object",
              "required": ["summary", "tags"],
              "properties": {
                "summary": { "type": "string" },
                "tags": { "type": "array", "items": { "type": "string" } },
                "language": { "type": "string" },
                "sensitivity": { "type": "string" }
              }
            }
            """,
            MaxOutputTokens: 800,
            MaxAttempts: 1));
        registry.Register(new AiSchemaDefinition(
            SuggestionsSchemaName,
            SchemaVersion,
            """
            {
              "type": "object",
              "required": ["suggestions"],
              "properties": {
                "suggestions": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "required": ["suggestionType", "title", "reason", "confidence", "payload"],
                    "properties": {
                      "suggestionType": {
                        "type": "string",
                        "enum": ["rename", "move", "tag", "duplicate", "stale", "unfiled"]
                      },
                      "title": { "type": "string" },
                      "reason": { "type": "string" },
                      "confidence": { "type": "number" },
                      "payload": { "type": "object" }
                    }
                  }
                }
              }
            }
            """,
            MaxOutputTokens: 1200,
            MaxAttempts: 1));
    }

    private async Task<FileAiContext> LoadContextAsync(Guid fileItemId, CancellationToken ct)
    {
        var item = await db.Set<FileItemEntity>()
            .Include(file => file.Provider)
            .FirstOrDefaultAsync(file =>
                file.Id == fileItemId
                && file.Provider != null
                && file.Provider.UserId == UserId
                && !file.IsDeleted,
                ct)
            ?? throw new DomainException(5300, "File item not found");
        if (item.CurrentVersionId is null)
            throw new DomainException(5304, "File version not found");

        var version = await db.Set<FileVersionEntity>()
            .FirstOrDefaultAsync(candidate =>
                candidate.Id == item.CurrentVersionId
                && candidate.FileItemId == item.Id
                && candidate.Source == "current"
                && candidate.IsCurrent,
                ct)
            ?? throw new DomainException(5304, "File version not found");
        var chunks = await db.Set<FileChunkEntity>()
            .AsNoTracking()
            .Where(chunk => chunk.FileItemId == item.Id && chunk.VersionId == version.Id)
            .OrderBy(chunk => chunk.ChunkIndex)
            .Take(8)
            .ToListAsync(ct);

        if (chunks.Count == 0)
            throw new DomainException(5311, "File has no indexed evidence chunks");

        return new FileAiContext(item, version, chunks);
    }

    private AiGatewayRequest BuildGatewayRequest(
        FileAiContext context,
        string purpose,
        string schemaName,
        string prompt)
        => new(
            UserId,
            "files",
            purpose,
            "file",
            context.Item.Id,
            [
                new AiGatewayMessage("system", "You analyze indexed file evidence and return only JSON matching the requested schema."),
                new AiGatewayMessage("user", prompt)
            ],
            SchemaName: schemaName,
            SchemaVersion: SchemaVersion,
            MaxOutputTokens: schemaName == SummarySchemaName ? 800 : 1200,
            MaxAttempts: 1,
            Metadata: BuildMetadata(context));

    private static IReadOnlyDictionary<string, string> BuildMetadata(FileAiContext context)
        => new Dictionary<string, string>
        {
            ["fileId"] = context.Item.Id.ToString(),
            ["versionId"] = context.Version.Id.ToString(),
            ["evidenceChunkIds"] = string.Join(",", context.Chunks.Select(chunk => chunk.Id))
        };

    private static string BuildSummaryPrompt(FileAiContext context)
        => $"""
            Summarize this file and produce tags.

            File:
            id: {context.Item.Id}
            versionId: {context.Version.Id}
            path: {context.Item.Path}
            name: {context.Item.Name}
            mimeType: {context.Item.MimeType}
            modifiedAt: {context.Item.ModifiedAt:O}

            Evidence chunks:
            {FormatChunks(context.Chunks)}
            """;

    private static string BuildSuggestionsPrompt(FileAiContext context)
        => $"""
            Suggest non-executing organization improvements for this file.

            File:
            id: {context.Item.Id}
            versionId: {context.Version.Id}
            path: {context.Item.Path}
            name: {context.Item.Name}
            mimeType: {context.Item.MimeType}
            modifiedAt: {context.Item.ModifiedAt:O}

            Evidence chunks:
            {FormatChunks(context.Chunks)}
            """;

    private static string FormatChunks(IReadOnlyList<FileChunkEntity> chunks)
        => string.Join(
            Environment.NewLine,
            chunks.Select(chunk => $"[{chunk.Id}] {chunk.Text}"));

    private static bool IsSuccessful(AiGatewayResult result)
        => string.Equals(result.Status, "succeeded", StringComparison.OrdinalIgnoreCase)
            && (result.ParsedOutput is not null || !string.IsNullOrWhiteSpace(result.ResponseText));

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var propertyElement)
            && propertyElement.ValueKind == JsonValueKind.String
            ? propertyElement.GetString()
            : null;

    private static FileAiResultDto MapAiResult(FileAiResultEntity entity)
        => new(
            entity.Id,
            entity.FileItemId,
            entity.VersionId,
            entity.Summary,
            JsonSerializer.Deserialize<IReadOnlyList<string>>(entity.TagsJson) ?? [],
            entity.Language,
            entity.Sensitivity,
            entity.GeneratedAt,
            entity.Model,
            entity.AiRequestLogId,
            JsonSerializer.Deserialize<IReadOnlyList<Guid>>(entity.EvidenceChunkIdsJson) ?? []);

    private static FileSuggestionDto MapSuggestion(FileSuggestionEntity suggestion)
        => new(
            suggestion.Id,
            suggestion.FileItemId,
            suggestion.SuggestionType,
            suggestion.Title,
            suggestion.Reason,
            suggestion.Confidence,
            suggestion.PayloadJson,
            suggestion.Status,
            suggestion.AiRequestLogId,
            suggestion.CreatedAt,
            suggestion.UpdatedAt);

    private sealed record FileAiContext(
        FileItemEntity Item,
        FileVersionEntity Version,
        IReadOnlyList<FileChunkEntity> Chunks);
}
