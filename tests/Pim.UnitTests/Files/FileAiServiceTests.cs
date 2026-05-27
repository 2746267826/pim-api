using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Ai;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Services;
using Xunit;

namespace Pim.UnitTests.Files;

public class FileAiServiceTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task GenerateSummaryAndTagsAsync_SendsGovernedGatewayRequestAndStoresResult()
    {
        await using var db = CreateDb();
        var (item, version, chunks) = await SeedIndexedFileAsync(db);
        var logId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var gateway = new FakeAiGateway
        {
            Result = SuccessfulResult(
                logId,
                """
                {
                  "summary": "A budget planning report.",
                  "tags": ["budget", "planning"],
                  "language": "en",
                  "sensitivity": "normal"
                }
                """,
                "test-model")
        };
        var service = CreateService(db, gateway);

        var result = await service.GenerateSummaryAndTagsAsync(item.Id);

        Assert.NotNull(result);
        Assert.Equal(item.Id, result.FileItemId);
        Assert.Equal(version.Id, result.VersionId);
        Assert.Equal("A budget planning report.", result.Summary);
        Assert.Equal(["budget", "planning"], result.Tags);
        Assert.Equal(logId, result.AiRequestLogId);
        Assert.Equal(chunks.Select(chunk => chunk.Id).ToArray(), result.EvidenceChunkIds.ToArray());

        var request = Assert.Single(gateway.Requests);
        Assert.Equal("files", request.Module);
        Assert.Equal("file.summary", request.Purpose);
        Assert.Equal("file", request.SourceObjectType);
        Assert.Equal(item.Id.ToString(), request.SourceObjectId);
        Assert.Equal("files.summary.v1", request.SchemaName);
        Assert.Equal("1", request.SchemaVersion);
        Assert.Contains(item.Path, request.Messages.Last().Content, StringComparison.Ordinal);
        Assert.Contains(chunks[0].Id.ToString(), request.Messages.Last().Content, StringComparison.Ordinal);
        Assert.DoesNotContain("protected:", request.Messages.Last().Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(item.Id.ToString(), request.Metadata!["fileId"]);
        Assert.Equal(version.Id.ToString(), request.Metadata!["versionId"]);
        Assert.Equal(string.Join(",", chunks.Select(chunk => chunk.Id)), request.Metadata!["evidenceChunkIds"]);

        var saved = await db.Set<FileAiResultEntity>().SingleAsync();
        Assert.Equal(result.Id, saved.Id);
        Assert.Equal(logId, saved.AiRequestLogId);
    }

    [Fact]
    public async Task GenerateOrganizationSuggestionsAsync_StoresPendingSuggestions()
    {
        await using var db = CreateDb();
        var (item, _, _) = await SeedIndexedFileAsync(db);
        var logId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var gateway = new FakeAiGateway
        {
            Result = SuccessfulResult(
                logId,
                """
                {
                  "suggestions": [
                    {
                      "suggestionType": "rename",
                      "title": "Use clearer report name",
                      "reason": "The document title mentions the final 2026 budget report.",
                      "confidence": 0.82,
                      "payload": { "proposedName": "2026-budget-final-report.docx" }
                    }
                  ]
                }
                """,
                "test-model")
        };
        var service = CreateService(db, gateway);

        var suggestions = await service.GenerateOrganizationSuggestionsAsync(item.Id);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(item.Id, suggestion.FileItemId);
        Assert.Equal("rename", suggestion.SuggestionType);
        Assert.Equal("pending", suggestion.Status);
        Assert.Equal(logId, suggestion.AiRequestLogId);
        Assert.Contains("2026-budget-final-report.docx", suggestion.PayloadJson, StringComparison.Ordinal);

        var request = Assert.Single(gateway.Requests);
        Assert.Equal("file.organization_suggestions", request.Purpose);
        Assert.Equal("files.organization_suggestions.v1", request.SchemaName);
    }

    [Fact]
    public async Task GenerateOrganizationSuggestionsAsync_WhenGatewayFailsCreatesNoSuggestions()
    {
        await using var db = CreateDb();
        var (item, _, _) = await SeedIndexedFileAsync(db);
        var gateway = new FakeAiGateway
        {
            Result = new AiResult(
                AiRequestStatus.Failed,
                null,
                null,
                [],
                new AiTokenUsage(null, null, null, null, null),
                Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                "AI failed.")
        };
        var service = CreateService(db, gateway);

        var suggestions = await service.GenerateOrganizationSuggestionsAsync(item.Id);

        Assert.Empty(suggestions);
        Assert.Empty(await db.Set<FileSuggestionEntity>().ToListAsync());
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"file-ai-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static FileAiService CreateService(PimDbContext db, FakeAiGateway gateway)
        => new(db, new FixedCurrentUserService(UserId), gateway);

    private static async Task<(FileItemEntity Item, FileVersionEntity Version, List<FileChunkEntity> Chunks)> SeedIndexedFileAsync(
        PimDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var provider = new FileProviderEntity
        {
            UserId = UserId,
            BaseUrl = "https://cloud.example.test",
            InternalBaseUrl = "http://nextcloud",
            Username = "alice",
            AppPasswordSecret = "protected:app-password",
            Status = "connected",
            CreatedAt = now,
            UpdatedAt = now
        };
        var item = new FileItemEntity
        {
            Provider = provider,
            ExternalFileId = "file-1",
            Path = "/Reports/budget.txt",
            Name = "budget.txt",
            ItemType = "file",
            MimeType = "text/plain",
            Size = 100,
            Etag = "etag-1",
            Permissions = "RGDNVW",
            LastSeenAt = now,
            CreatedAt = now,
            ModifiedAt = now,
            SyncedAt = now
        };
        var version = new FileVersionEntity
        {
            FileItem = item,
            ExternalVersionId = "current:etag-1",
            Etag = "etag-1",
            Source = "current",
            IsCurrent = true,
            ModifiedAt = now,
            SyncedAt = now
        };
        var chunks = Enumerable.Range(0, 3)
            .Select(index => new FileChunkEntity
            {
                FileItem = item,
                Version = version,
                ChunkIndex = index,
                Text = $"chunk {index} budget evidence",
                TextHash = $"hash-{index}",
                StartOffset = index * 10,
                EndOffset = index * 10 + 9
            })
            .ToList();

        db.Set<FileProviderEntity>().Add(provider);
        db.Set<FileItemEntity>().Add(item);
        db.Set<FileVersionEntity>().Add(version);
        db.Set<FileChunkEntity>().AddRange(chunks);
        await db.SaveChangesAsync();
        item.CurrentVersionId = version.Id;
        await db.SaveChangesAsync();

        return (item, version, chunks);
    }

    private static AiResult SuccessfulResult(Guid logId, string json, string model)
        => new(
            AiRequestStatus.Succeeded,
            json,
            json,
            [],
            new AiTokenUsage(10, 20, 30, null, null),
            logId,
            null);

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }

    private sealed class FakeAiGateway : IAiGateway
    {
        public List<AiGatewayRequest> Requests { get; } = [];
        public AiResult Result { get; set; } = new(
            AiRequestStatus.Failed,
            null,
            null,
            [],
            new AiTokenUsage(null, null, null, null, null),
            null,
            "No result configured.");

        public Task<AiResult> CompleteAsync(AiGatewayRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }
}
