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
        using var db = CreateDb();

        var connection = db.Model.FindEntityType(typeof(OutlookConnectionEntity))!;
        Assert.NotNull(connection.FindProperty(nameof(OutlookConnectionEntity.MsalCacheEncrypted)));
        Assert.NotNull(connection.FindProperty(nameof(OutlookConnectionEntity.HomeAccountId)));
        Assert.True(connection.FindIndex(connection.FindProperty(nameof(OutlookConnectionEntity.UserId))!)!.IsUnique);
        Assert.True(connection.FindProperty(nameof(OutlookConnectionEntity.Version))!.IsConcurrencyToken);

        var binding = db.Model.FindEntityType(typeof(OutlookCalendarBindingEntity))!;
        Assert.True(binding.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(OutlookCalendarBindingEntity.ConnectionId),
                nameof(OutlookCalendarBindingEntity.GraphCalendarId)])).IsUnique);
        Assert.Equal(
            DeleteBehavior.Cascade,
            Assert.Single(binding.GetForeignKeys(), foreignKey =>
                foreignKey.PrincipalEntityType.ClrType == typeof(OutlookConnectionEntity)).DeleteBehavior);
        Assert.Equal(
            DeleteBehavior.Restrict,
            Assert.Single(binding.GetForeignKeys(), foreignKey =>
                foreignKey.PrincipalEntityType.ClrType == typeof(CalendarEntity)).DeleteBehavior);

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

    [Fact]
    public void MicrosoftSyncModel_OutlookEventRelationshipsUseSetNull()
    {
        using var db = CreateDb();
        var outlookEvent = db.Model.FindEntityType(typeof(EventEntity))!;

        var bindingForeignKey = Assert.Single(outlookEvent.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(OutlookCalendarBindingEntity) &&
            foreignKey.Properties.Select(property => property.Name).SequenceEqual([
                nameof(EventEntity.OutlookCalendarBindingId)]));
        Assert.Equal(DeleteBehavior.SetNull, bindingForeignKey.DeleteBehavior);

        var connectionForeignKey = Assert.Single(outlookEvent.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(OutlookConnectionEntity) &&
            foreignKey.Properties.Select(property => property.Name).SequenceEqual([
                nameof(EventEntity.OutlookConnectionId)]));
        Assert.Equal(DeleteBehavior.SetNull, connectionForeignKey.DeleteBehavior);
    }

    [Fact]
    public void MicrosoftSyncModel_OutlookEventIdentityIgnoresSoftDeletedRows()
    {
        using var db = CreateDb();
        var outlookEvent = db.Model.FindEntityType(typeof(EventEntity))!;
        var index = Assert.Single(outlookEvent.GetIndexes(), candidate =>
            candidate.Properties.Select(property => property.Name).SequenceEqual([
                nameof(EventEntity.OutlookCalendarBindingId),
                nameof(EventEntity.OutlookEventId)]));

        Assert.True(index.IsUnique);
        Assert.Equal(
            "\"outlook_calendar_binding_id\" IS NOT NULL AND \"outlook_event_id\" IS NOT NULL AND \"deleted_at\" IS NULL",
            index.GetFilter());
    }

    [Fact]
    public void MicrosoftSyncModel_AuthorizationSessionHasConnectionIntegrity()
    {
        using var db = CreateDb();
        var session = db.Model.FindEntityType(typeof(OutlookAuthorizationSessionEntity))!;

        Assert.True(session.FindProperty(nameof(OutlookAuthorizationSessionEntity.Version))!.IsConcurrencyToken);
        var connectionForeignKey = Assert.Single(session.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(OutlookConnectionEntity));
        Assert.Equal(DeleteBehavior.Cascade, connectionForeignKey.DeleteBehavior);
        var activeConnection = Assert.Single(session.GetIndexes(), index =>
            index.GetDatabaseName() == "UX_outlook_authorization_sessions_active_connection");
        Assert.True(activeConnection.IsUnique);
        Assert.Equal("\"status\" IN ('starting', 'waiting-for-user')", activeConnection.GetFilter());
        Assert.Contains(session.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(OutlookAuthorizationSessionEntity.ConnectionId),
                nameof(OutlookAuthorizationSessionEntity.Status)]));
    }

    [Fact]
    public void EventEntity_HasNewUnifiedFields()
    {
        using var db = CreateDb();
        var entity = db.Model.FindEntityType(typeof(EventEntity))!;

        Assert.NotNull(entity.FindProperty("DescriptionFormat"));
        Assert.NotNull(entity.FindProperty("ShowAs"));
        Assert.NotNull(entity.FindProperty("Importance"));
        Assert.NotNull(entity.FindProperty("Sensitivity"));
        Assert.NotNull(entity.FindProperty("CategoriesJson"));
        Assert.NotNull(entity.FindProperty("IsReminderOn"));
        Assert.NotNull(entity.FindProperty("ReminderMinutesBeforeStart"));
        Assert.NotNull(entity.FindProperty("OrganizerJson"));
        Assert.NotNull(entity.FindProperty("AttendeesJson"));
        Assert.NotNull(entity.FindProperty("IsOnlineMeeting"));
        Assert.NotNull(entity.FindProperty("OnlineMeetingProvider"));
        Assert.NotNull(entity.FindProperty("OnlineMeetingUrl"));
        Assert.NotNull(entity.FindProperty("ExternalLink"));
        Assert.NotNull(entity.FindProperty("AttachmentReferencesJson"));
    }

    [Fact]
    public void EventEntity_StillHasLegacyOrganizer()
    {
        using var db = CreateDb();
        var entity = db.Model.FindEntityType(typeof(EventEntity))!;
        Assert.NotNull(entity.FindProperty("Organizer"));
    }

    [Fact]
    public void EventEntity_CollectionColumnsAreJsonb()
    {
        using var db = CreateDb();
        var entity = db.Model.FindEntityType(typeof(EventEntity))!;

        Assert.NotNull(entity.FindProperty("CategoriesJson"));
        Assert.Equal("jsonb", entity.FindProperty("CategoriesJson")!.GetColumnType());

        Assert.NotNull(entity.FindProperty("OrganizerJson"));
        Assert.Equal("jsonb", entity.FindProperty("OrganizerJson")!.GetColumnType());

        Assert.NotNull(entity.FindProperty("AttendeesJson"));
        Assert.Equal("jsonb", entity.FindProperty("AttendeesJson")!.GetColumnType());

        Assert.NotNull(entity.FindProperty("AttachmentReferencesJson"));
        Assert.Equal("jsonb", entity.FindProperty("AttachmentReferencesJson")!.GetColumnType());
    }

    [Fact]
    public void EventEntity_CollectionColumnsDefaultToEmptyArray()
    {
        using var db = CreateDb();
        var entity = db.Model.FindEntityType(typeof(EventEntity))!;

        Assert.NotNull(entity.FindProperty("CategoriesJson"));
        Assert.Equal("[]", entity.FindProperty("CategoriesJson")!.GetDefaultValue());

        Assert.NotNull(entity.FindProperty("AttendeesJson"));
        Assert.Equal("[]", entity.FindProperty("AttendeesJson")!.GetDefaultValue());

        Assert.NotNull(entity.FindProperty("AttachmentReferencesJson"));
        Assert.Equal("[]", entity.FindProperty("AttachmentReferencesJson")!.GetDefaultValue());
    }

    [Fact]
    public void EventEntity_BoolColumnsDefaultToFalse()
    {
        using var db = CreateDb();
        var entity = db.Model.FindEntityType(typeof(EventEntity))!;

        Assert.NotNull(entity.FindProperty("IsReminderOn"));
        Assert.Equal(false, entity.FindProperty("IsReminderOn")!.GetDefaultValue());

        Assert.NotNull(entity.FindProperty("IsOnlineMeeting"));
        Assert.Equal(false, entity.FindProperty("IsOnlineMeeting")!.GetDefaultValue());
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(OutlookConnectionEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql("Host=localhost;Database=pim_model_tests")
            .Options;
        return new PimDbContext(options);
    }
}
