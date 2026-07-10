using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;
using Xunit;

namespace Pim.UnitTests.Calendar;

public sealed class OutlookPersistenceModelTests
{
    [Fact]
    public void MicrosoftSyncModel_HasPerCalendarAndDurableExecutionConstraints()
    {
        PimDbContext.RegisterModuleAssembly(typeof(OutlookConnectionEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"outlook-model-{Guid.NewGuid()}")
            .Options;
        using var db = new PimDbContext(options);

        var connection = db.Model.FindEntityType(typeof(OutlookConnectionEntity))!;
        Assert.NotNull(connection.FindProperty(nameof(OutlookConnectionEntity.MsalCacheEncrypted)));
        Assert.NotNull(connection.FindProperty(nameof(OutlookConnectionEntity.HomeAccountId)));
        Assert.True(connection.FindIndex(connection.FindProperty(nameof(OutlookConnectionEntity.UserId))!)!.IsUnique);

        var binding = db.Model.FindEntityType(typeof(OutlookCalendarBindingEntity))!;
        Assert.True(binding.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(OutlookCalendarBindingEntity.ConnectionId),
                nameof(OutlookCalendarBindingEntity.GraphCalendarId)])).IsUnique);

        var execution = db.Model.FindEntityType(typeof(OutlookOperationExecutionEntity))!;
        Assert.True(execution.GetIndexes().Single(index =>
            index.Properties.Count == 1 &&
            index.Properties[0].Name == nameof(OutlookOperationExecutionEntity.ConfirmationId)).IsUnique);

        var conflict = db.Model.FindEntityType(typeof(SyncConflictEntity))!;
        Assert.NotNull(conflict.FindProperty(nameof(SyncConflictEntity.SourceConfirmationId)));
        Assert.NotNull(conflict.FindIndex(conflict.FindProperty(nameof(SyncConflictEntity.SourceConfirmationId))!));

        var outlookEvent = db.Model.FindEntityType(typeof(EventEntity))!;
        Assert.True(outlookEvent.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(EventEntity.OutlookCalendarBindingId),
                nameof(EventEntity.OutlookEventId)])).IsUnique);
    }
}
