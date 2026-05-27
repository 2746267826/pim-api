using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Xunit;

namespace Pim.UnitTests.Ai;

public class AiPersistenceModelTests
{
    [Fact]
    public async Task AiRequestLogs_PersistCompleteAttemptTrace()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new PimDbContext(options);
        var id = Guid.Parse("22222222-2222-2222-2222-222222222222");

        db.AiRequestLogs.Add(new AiRequestLogEntity
        {
            Id = id,
            UserId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Module = "quick-notes",
            Purpose = "quick-notes.convert",
            SourceObjectType = "quick_note",
            SourceObjectId = "note-1",
            Provider = "litellm",
            Model = "pim-default",
            CorrelationId = "corr-1",
            Status = "succeeded",
            AttemptNumber = 1,
            MaxAttempts = 1,
            RequestMessagesJson = "[{\"role\":\"user\",\"content\":\"hello\"}]",
            RequestPayloadJson = "{\"model\":\"pim-default\"}",
            ResponseRawJson = "{\"id\":\"chatcmpl-1\"}",
            ResponseText = "{\"title\":\"Hello\"}",
            ParsedOutputJson = "{\"title\":\"Hello\"}",
            SchemaName = "quick-note-conversion",
            SchemaVersion = "1",
            SchemaJsonSnapshot = "{\"type\":\"object\"}",
            SchemaValidationErrorsJson = "[]",
            PromptTokens = 4,
            CompletionTokens = 6,
            TotalTokens = 10,
            EstimatedCost = 0.00012m,
            Currency = "USD",
            InputChars = 5,
            OutputChars = 17,
            InputHash = "input-hash",
            OutputHash = "output-hash",
            MetadataJson = "{\"origin\":\"unit-test\"}"
        });
        await db.SaveChangesAsync();

        var saved = await db.AiRequestLogs.SingleAsync(l => l.Id == id);
        Assert.Equal("litellm", saved.Provider);
        Assert.Equal(10, saved.TotalTokens);
        Assert.Equal("{\"title\":\"Hello\"}", saved.ParsedOutputJson);
    }

    [Fact]
    public async Task AiProviderSettings_PersistSystemProviderState()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new PimDbContext(options);
        db.AiProviderSettings.Add(new AiProviderSettingEntity
        {
            Provider = "litellm",
            BaseUrl = "http://litellm:4000",
            VirtualKeySecret = "encrypted-secret",
            DefaultModel = "pim-default",
            Status = "enabled"
        });
        await db.SaveChangesAsync();

        var saved = await db.AiProviderSettings.SingleAsync();
        Assert.Equal("litellm", saved.Provider);
        Assert.Equal("http://litellm:4000", saved.BaseUrl);
    }
}
