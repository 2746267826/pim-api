using Microsoft.EntityFrameworkCore;
using Pim.Core.Ai;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Data;
using Xunit;

namespace Pim.UnitTests.Ai;

public class AiRequestLogWriterTests
{
    [Fact]
    public async Task WriteAsync_PersistsFailuresWithRedactedPayloads()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new PimDbContext(options);
        var writer = new AiRequestLogWriter(db);

        var id = await writer.WriteAsync(new AiRequestLogWriteModel(
            UserId: null,
            Module: "files",
            Purpose: "files.summarize",
            SourceObjectType: "file",
            SourceObjectId: "file-1",
            Provider: "litellm",
            Model: "pim-default",
            LiteLlmRequestId: null,
            CorrelationId: "corr-1",
            Status: AiRequestStatus.Failed,
            AttemptNumber: 1,
            MaxAttempts: 1,
            StartedAt: DateTimeOffset.UtcNow,
            FinishedAt: DateTimeOffset.UtcNow.AddMilliseconds(40),
            RequestMessagesJson: """[{"role":"user","content":"hello"}]""",
            RequestPayloadJson: """{"api_key":"sk-secret","model":"pim-default"}""",
            ResponseRawJson: """{"error":"bad key"}""",
            ResponseText: null,
            ParsedOutputJson: null,
            SchemaName: null,
            SchemaVersion: null,
            SchemaJsonSnapshot: null,
            SchemaValidationErrorsJson: "[]",
            PromptTokens: null,
            CompletionTokens: null,
            TotalTokens: null,
            EstimatedCost: null,
            Currency: null,
            ErrorCode: "provider_unavailable",
            ErrorMessage: "LiteLLM returned 401",
            MetadataJson: """{"Authorization":"Bearer secret"}"""), CancellationToken.None);

        var saved = await db.AiRequestLogs.SingleAsync(l => l.Id == id);
        Assert.Equal("failed", saved.Status);
        Assert.DoesNotContain("sk-secret", saved.RequestPayloadJson);
        Assert.DoesNotContain("secret", saved.MetadataJson);
        Assert.Equal("provider_unavailable", saved.ErrorCode);
    }

    [Fact]
    public async Task WriteAsync_PreservesPlainResponseText()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new PimDbContext(options);
        var writer = new AiRequestLogWriter(db);

        var id = await writer.WriteAsync(new AiRequestLogWriteModel(
            UserId: null,
            Module: "quick-notes",
            Purpose: "quick-notes.convert",
            SourceObjectType: "quick_note",
            SourceObjectId: "note-1",
            Provider: "litellm",
            Model: "pim-default",
            LiteLlmRequestId: "req-1",
            CorrelationId: "corr-2",
            Status: AiRequestStatus.Succeeded,
            AttemptNumber: 1,
            MaxAttempts: 1,
            StartedAt: DateTimeOffset.UtcNow,
            FinishedAt: DateTimeOffset.UtcNow.AddMilliseconds(25),
            RequestMessagesJson: """[{"role":"user","content":"summarize this"}]""",
            RequestPayloadJson: """{"model":"pim-default"}""",
            ResponseRawJson: """{"id":"chatcmpl-1"}""",
            ResponseText: "plain response text",
            ParsedOutputJson: null,
            SchemaName: null,
            SchemaVersion: null,
            SchemaJsonSnapshot: null,
            SchemaValidationErrorsJson: "[]",
            PromptTokens: 3,
            CompletionTokens: 4,
            TotalTokens: 7,
            EstimatedCost: 0.0001m,
            Currency: "USD",
            ErrorCode: null,
            ErrorMessage: null,
            MetadataJson: "{}"), CancellationToken.None);

        var saved = await db.AiRequestLogs.SingleAsync(l => l.Id == id);
        Assert.Equal("plain response text", saved.ResponseText);
        Assert.Equal("succeeded", saved.Status);
    }
}
