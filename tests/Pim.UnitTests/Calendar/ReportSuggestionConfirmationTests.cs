using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class ReportSuggestionConfirmationTests
{
    private static readonly Guid UserId = Guid.Parse("91919191-9191-9191-9191-919191919191");

    [Fact]
    public async Task ActionableReportSuggestionCreatesConfirmationInsteadOfChangingFacts()
    {
        await using var db = CreateDb();
        var report = new ReportArtifactEntity
        {
            UserId = UserId,
            Kind = "Daily",
            ContentMarkdown = "# Daily",
            InputsJson = "{}",
            MetricsJson = "{}"
        };
        var suggestion = new ReportSuggestionEntity
        {
            UserId = UserId,
            Report = report,
            ReportId = report.Id,
            Action = "move-task-segment",
            Summary = "Move focus block",
            ChangedFieldsJson = """["startsAt","endsAt"]""",
            PayloadJson = """{"startsAt":"2026-07-08T10:00:00Z"}"""
        };
        db.Set<ReportArtifactEntity>().Add(report);
        db.Set<ReportSuggestionEntity>().Add(suggestion);
        await db.SaveChangesAsync();
        var service = new ReportService(
            db,
            new FixedCurrentUserService(UserId),
            new OperationConfirmationService(db));

        var confirmation = await service.RequestSuggestionActionAsync(suggestion.Id, CancellationToken.None);

        Assert.Equal(OperationRiskLevel.L2PimFactChange, confirmation.RiskLevel);
        Assert.Contains("startsAt", confirmation.ChangedFields ?? []);
        Assert.Equal(0, await db.Set<TaskExecutionSegmentEntity>().CountAsync());
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(ReportArtifactEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"report-suggestion-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
