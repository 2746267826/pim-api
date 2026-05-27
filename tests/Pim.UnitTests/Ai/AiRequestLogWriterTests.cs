using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Ai;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Data;
using Xunit;

namespace Pim.UnitTests.Ai;

public class AiRequestLogWriterTests
{
    public static TheoryData<AiRequestStatus, string> StatusCases => new()
    {
        { AiRequestStatus.Succeeded, "succeeded" },
        { AiRequestStatus.Failed, "failed" },
        { AiRequestStatus.Blocked, "blocked" },
        { AiRequestStatus.TimedOut, "timed_out" },
        { AiRequestStatus.FailedValidation, "failed_validation" }
    };

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

    [Fact]
    public async Task WriteAsync_RedactsEveryPersistedStringField()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new PimDbContext(options);
        var writer = new AiRequestLogWriter(db);
        const string canary = "sk-task3-canary-1234567890";

        var id = await writer.WriteAsync(new AiRequestLogWriteModel(
            UserId: null,
            Module: "files",
            Purpose: "files.summarize",
            SourceObjectType: "file",
            SourceObjectId: "file-1",
            Provider: "litellm",
            Model: "pim-default",
            LiteLlmRequestId: "req-1",
            CorrelationId: "corr-3",
            Status: AiRequestStatus.Failed,
            AttemptNumber: 1,
            MaxAttempts: 2,
            StartedAt: DateTimeOffset.Parse("2026-05-27T01:00:00Z"),
            FinishedAt: DateTimeOffset.Parse("2026-05-27T01:00:01Z"),
            RequestMessagesJson: $$"""[{"role":"user","content":"message {{canary}}"}]""",
            RequestPayloadJson: $$"""{"api_key":"{{canary}}","client_secret":"plain-secret"}""",
            ResponseRawJson: $$"""{"authorization":"Bearer {{canary}}"}""",
            ResponseText: $"text {canary}",
            ParsedOutputJson: $$"""{"result":"{{canary}}"}""",
            SchemaName: "schema",
            SchemaVersion: "1",
            SchemaJsonSnapshot: $$"""{"x-api-key":"{{canary}}"}""",
            SchemaValidationErrorsJson: $$"""["schema error {{canary}}"]""",
            PromptTokens: null,
            CompletionTokens: null,
            TotalTokens: null,
            EstimatedCost: null,
            Currency: null,
            ErrorCode: "provider_unavailable",
            ErrorMessage: $"failed with {canary}",
            MetadataJson: $$"""{"token":"{{canary}}"}"""), CancellationToken.None);

        var saved = await db.AiRequestLogs.SingleAsync(l => l.Id == id);
        var stringValues = typeof(Pim.Infrastructure.Data.Entities.AiRequestLogEntity)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => (string?)p.GetValue(saved))
            .Where(v => v is not null);

        foreach (var value in stringValues)
        {
            Assert.DoesNotContain(canary, value);
        }
    }

    [Fact]
    public async Task WriteAsync_InvalidJsonInputs_AreStoredAsParseableJson()
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
            CorrelationId: "corr-4",
            Status: AiRequestStatus.Failed,
            AttemptNumber: 1,
            MaxAttempts: 1,
            StartedAt: DateTimeOffset.Parse("2026-05-27T02:00:00Z"),
            FinishedAt: DateTimeOffset.Parse("2026-05-27T02:00:00.050Z"),
            RequestMessagesJson: "[]",
            RequestPayloadJson: "not json sk-task3-canary-1234567890",
            ResponseRawJson: "not json Bearer secret-token",
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
            ErrorMessage: null,
            MetadataJson: "{}"), CancellationToken.None);

        var saved = await db.AiRequestLogs.SingleAsync(l => l.Id == id);
        JsonDocument.Parse(saved.RequestPayloadJson).Dispose();
        JsonDocument.Parse(saved.ResponseRawJson).Dispose();
        Assert.DoesNotContain("sk-task3-canary-1234567890", saved.RequestPayloadJson);
        Assert.DoesNotContain("secret-token", saved.ResponseRawJson);
    }

    [Fact]
    public async Task WriteAsync_ComputesDeterministicDurationCharsAndHashes()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new PimDbContext(options);
        var writer = new AiRequestLogWriter(db);
        var startedAt = DateTimeOffset.Parse("2026-05-27T03:00:00Z");
        var finishedAt = startedAt.AddMilliseconds(125);

        var id = await writer.WriteAsync(new AiRequestLogWriteModel(
            UserId: null,
            Module: "quick-notes",
            Purpose: "quick-notes.convert",
            SourceObjectType: "quick_note",
            SourceObjectId: "note-2",
            Provider: "litellm",
            Model: "pim-default",
            LiteLlmRequestId: null,
            CorrelationId: "corr-5",
            Status: AiRequestStatus.Succeeded,
            AttemptNumber: 1,
            MaxAttempts: 1,
            StartedAt: startedAt,
            FinishedAt: finishedAt,
            RequestMessagesJson: """[{"role":"user","content":"hello"}]""",
            RequestPayloadJson: """{"model":"pim-default"}""",
            ResponseRawJson: """{"id":"chatcmpl-1"}""",
            ResponseText: "plain response text",
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
            ErrorCode: null,
            ErrorMessage: null,
            MetadataJson: "{}"), CancellationToken.None);

        var saved = await db.AiRequestLogs.SingleAsync(l => l.Id == id);
        var expectedInput = saved.RequestMessagesJson + saved.RequestPayloadJson;
        var expectedOutput = saved.ResponseText + saved.ResponseRawJson;

        Assert.Equal(125, saved.DurationMs);
        Assert.Equal(expectedInput.Length, saved.InputChars);
        Assert.Equal(expectedOutput.Length, saved.OutputChars);
        Assert.Equal(Sha256(expectedInput), saved.InputHash);
        Assert.Equal(Sha256(expectedOutput), saved.OutputHash);
    }

    [Theory]
    [MemberData(nameof(StatusCases))]
    public async Task WriteAsync_MapsStatuses(AiRequestStatus status, string expected)
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
            CorrelationId: "corr-status",
            Status: status,
            AttemptNumber: 1,
            MaxAttempts: 1,
            StartedAt: DateTimeOffset.Parse("2026-05-27T04:00:00Z"),
            FinishedAt: DateTimeOffset.Parse("2026-05-27T04:00:00.001Z"),
            RequestMessagesJson: "[]",
            RequestPayloadJson: "{}",
            ResponseRawJson: "{}",
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
            ErrorCode: null,
            ErrorMessage: null,
            MetadataJson: "{}"), CancellationToken.None);

        var saved = await db.AiRequestLogs.SingleAsync(l => l.Id == id);
        Assert.Equal(expected, saved.Status);
    }

    [Fact]
    public async Task WriteAsync_RedactsResponseTextTokenLikeValues()
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
            SourceObjectId: "note-3",
            Provider: "litellm",
            Model: "pim-default",
            LiteLlmRequestId: null,
            CorrelationId: "corr-6",
            Status: AiRequestStatus.Succeeded,
            AttemptNumber: 1,
            MaxAttempts: 1,
            StartedAt: DateTimeOffset.Parse("2026-05-27T05:00:00Z"),
            FinishedAt: DateTimeOffset.Parse("2026-05-27T05:00:00.001Z"),
            RequestMessagesJson: "[]",
            RequestPayloadJson: "{}",
            ResponseRawJson: "{}",
            ResponseText: "token sk-task3-canary-1234567890",
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
            ErrorCode: null,
            ErrorMessage: null,
            MetadataJson: "{}"), CancellationToken.None);

        var saved = await db.AiRequestLogs.SingleAsync(l => l.Id == id);
        Assert.DoesNotContain("sk-task3-canary-1234567890", saved.ResponseText);
        Assert.Contains("[REDACTED]", saved.ResponseText);
    }

    [Fact]
    public async Task WriteAsync_RedactsPlainTextKeyValueSecrets()
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
            SourceObjectId: "note-4",
            Provider: "litellm",
            Model: "pim-default",
            LiteLlmRequestId: null,
            CorrelationId: "corr-7",
            Status: AiRequestStatus.Failed,
            AttemptNumber: 1,
            MaxAttempts: 1,
            StartedAt: DateTimeOffset.Parse("2026-05-27T06:00:00Z"),
            FinishedAt: DateTimeOffset.Parse("2026-05-27T06:00:00.001Z"),
            RequestMessagesJson: "[]",
            RequestPayloadJson: "{}",
            ResponseRawJson: "{}",
            ResponseText: "password: hunter2",
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
            ErrorMessage: "client_secret=plain-secret",
            MetadataJson: "{}"), CancellationToken.None);

        var saved = await db.AiRequestLogs.SingleAsync(l => l.Id == id);
        Assert.DoesNotContain("hunter2", saved.ResponseText);
        Assert.DoesNotContain("plain-secret", saved.ErrorMessage);
        Assert.Contains("[REDACTED]", saved.ResponseText);
        Assert.Contains("[REDACTED]", saved.ErrorMessage);
    }

    [Fact]
    public async Task WriteAsync_RedactsPrefixedPlainTextKeyValueSecrets()
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
            SourceObjectId: "note-5",
            Provider: "litellm",
            Model: "pim-default",
            LiteLlmRequestId: null,
            CorrelationId: "corr-8",
            Status: AiRequestStatus.Failed,
            AttemptNumber: 1,
            MaxAttempts: 1,
            StartedAt: DateTimeOffset.Parse("2026-05-27T07:00:00Z"),
            FinishedAt: DateTimeOffset.Parse("2026-05-27T07:00:00.001Z"),
            RequestMessagesJson: "[]",
            RequestPayloadJson: "{}",
            ResponseRawJson: "{}",
            ResponseText: "error: password: hunter2",
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
            ErrorMessage: "LiteLLM error: client_secret=plain-secret",
            MetadataJson: "{}"), CancellationToken.None);

        var saved = await db.AiRequestLogs.SingleAsync(l => l.Id == id);
        Assert.DoesNotContain("hunter2", saved.ResponseText);
        Assert.DoesNotContain("plain-secret", saved.ErrorMessage);
        Assert.Contains("error: password: [REDACTED]", saved.ResponseText);
        Assert.Contains("LiteLLM error: client_secret=[REDACTED]", saved.ErrorMessage);
    }

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
