using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class ReportServiceTests
{
    private static readonly Guid UserId = Guid.Parse("90909090-9090-9090-9090-909090909090");

    [Theory]
    [InlineData("Daily")]
    [InlineData("Weekly")]
    [InlineData("Monthly")]
    [InlineData("Project")]
    public async Task GeneratesReportArtifactWithoutMutatingFacts(string kind)
    {
        await using var db = CreateDb();
        var calendar = new CalendarEntity { UserId = UserId, Name = "Default", IsDefault = true };
        db.Set<CalendarEntity>().Add(calendar);
        db.Set<TaskEntity>().Add(new TaskEntity { UserId = UserId, Uid = "task@pim", Title = "Task" });
        await db.SaveChangesAsync();
        var beforeTasks = await db.Set<TaskEntity>().CountAsync();
        var service = CreateService(db);

        var report = await service.GenerateAsync(
            new GenerateReportRequest(kind, DateOnly.Parse("2026-07-08"), null),
            CancellationToken.None);

        Assert.Equal(kind, report.Kind);
        Assert.Equal("L0AutomaticArtifact", report.RiskLevel);
        Assert.NotEmpty(report.ContentMarkdown);
        Assert.Equal(beforeTasks, await db.Set<TaskEntity>().CountAsync());
        Assert.Empty(await db.OperationConfirmations.ToListAsync());
    }

    private static ReportService CreateService(PimDbContext db)
        => new(db, new FixedCurrentUserService(UserId), new OperationConfirmationService(db));

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(ReportArtifactEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"report-service-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
