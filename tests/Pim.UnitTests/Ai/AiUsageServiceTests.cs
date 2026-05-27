using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pim.Core.Ai;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Xunit;

namespace Pim.UnitTests.Ai;

public class AiUsageServiceTests
{
    [Fact]
    public async Task ListRequestsAsync_FiltersByModuleAndStatus()
    {
        await using var db = CreateDb();
        db.AiRequestLogs.Add(MakeLog("quick-notes", "succeeded", 10));
        db.AiRequestLogs.Add(MakeLog("files", "failed", 3));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.ListRequestsAsync(new AiRequestLogFilter(
            From: null, To: null, Module: "quick-notes", Purpose: null, SourceObjectType: null,
            SourceObjectId: null, Model: null, Status: AiRequestStatus.Succeeded, UserId: null));

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("quick-notes", result.Items[0].Module);
        Assert.Equal(AiRequestStatus.Succeeded, result.Items[0].Status);
    }

    [Fact]
    public async Task GetUsageSummaryAsync_GroupsByModulePurposeModelAndStatus()
    {
        await using var db = CreateDb();
        db.AiRequestLogs.Add(MakeLog("quick-notes", "succeeded", 10));
        db.AiRequestLogs.Add(MakeLog("quick-notes", "failed", 5));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var summary = await service.GetUsageSummaryAsync(null, null);

        Assert.Equal(2, summary.RequestCount);
        Assert.Equal(1, summary.SuccessCount);
        Assert.Equal(1, summary.FailureCount);
        Assert.Equal(15, summary.TotalTokens);
        Assert.Contains(summary.ByModule, group => group.GroupKey == "quick-notes" && group.RequestCount == 2);
        Assert.Contains(summary.ByStatus, group => group.GroupKey == "failed" && group.FailureCount == 1);
    }

    private static PimDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }

    private static AiUsageService CreateService(PimDbContext db)
        => new(db, Options.Create(new AiOptions
        {
            Enabled = true,
            Provider = "litellm",
            BaseUrl = "http://litellm:4000",
            DefaultModel = "pim-default"
        }));

    private static AiRequestLogEntity MakeLog(string module, string status, int totalTokens) => new()
    {
        Module = module,
        Purpose = $"{module}.test",
        SourceObjectType = "test",
        SourceObjectId = Guid.NewGuid().ToString("N"),
        Provider = "litellm",
        Model = "pim-default",
        CorrelationId = Guid.NewGuid().ToString("N"),
        Status = status,
        AttemptNumber = 1,
        MaxAttempts = 1,
        StartedAt = DateTimeOffset.UtcNow,
        FinishedAt = DateTimeOffset.UtcNow,
        DurationMs = 20,
        RequestMessagesJson = "[]",
        RequestPayloadJson = "{}",
        ResponseRawJson = "{}",
        SchemaValidationErrorsJson = "[]",
        PromptTokens = totalTokens / 2,
        CompletionTokens = totalTokens - (totalTokens / 2),
        TotalTokens = totalTokens,
        EstimatedCost = 0.001m,
        Currency = "USD",
        InputHash = "input",
        OutputHash = "output",
        MetadataJson = "{}"
    };
}
