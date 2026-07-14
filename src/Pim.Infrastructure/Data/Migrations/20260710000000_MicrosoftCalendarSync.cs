using Microsoft.EntityFrameworkCore.Migrations;

namespace Pim.Infrastructure.Data.Migrations;

public partial class MicrosoftCalendarSync : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("authority", "outlook_connections", maxLength: 512, nullable: false,
            defaultValue: "https://login.microsoftonline.com/common");
        migrationBuilder.AddColumn<string>("home_account_id", "outlook_connections", maxLength: 512, nullable: true);
        migrationBuilder.AddColumn<string>("account_display_name", "outlook_connections", maxLength: 255, nullable: true);
        migrationBuilder.AddColumn<string>("account_login_hint", "outlook_connections", maxLength: 255, nullable: true);
        migrationBuilder.AddColumn<byte[]>("msal_cache_encrypted", "outlook_connections", nullable: true);
        migrationBuilder.AddColumn<long>("version", "outlook_connections", nullable: false, defaultValue: 0L);

        migrationBuilder.AddColumn<Guid>("connection_id", "outlook_sync_batches", nullable: true);
        migrationBuilder.AddColumn<string>("mode", "outlook_sync_batches", maxLength: 32, nullable: false, defaultValue: "incremental");
        migrationBuilder.AddColumn<DateTimeOffset>("requested_window_start", "outlook_sync_batches", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("requested_window_end", "outlook_sync_batches", nullable: true);
        migrationBuilder.AddColumn<string>("requested_calendar_ids_json", "outlook_sync_batches", type: "jsonb", nullable: false, defaultValue: "[]");
        migrationBuilder.AddColumn<string>("per_calendar_json", "outlook_sync_batches", type: "jsonb", nullable: false, defaultValue: "[]");
        migrationBuilder.AddColumn<bool>("cancel_requested", "outlook_sync_batches", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<DateTimeOffset>("updated_at", "outlook_sync_batches", nullable: false, defaultValueSql: "now()");

        migrationBuilder.AddColumn<string>("source", "calendars", maxLength: 32, nullable: false, defaultValue: "manual");
        migrationBuilder.AddColumn<bool>("is_visible", "calendars", nullable: false, defaultValue: true);

        migrationBuilder.AddColumn<Guid>("outlook_connection_id", "events", nullable: true);
        migrationBuilder.AddColumn<Guid>("outlook_calendar_binding_id", "events", nullable: true);
        migrationBuilder.AddColumn<string>("outlook_series_master_id", "events", maxLength: 512, nullable: true);
        migrationBuilder.AddColumn<string>("outlook_event_type", "events", maxLength: 32, nullable: true);
        migrationBuilder.AddColumn<string>("original_start_time_zone", "events", maxLength: 128, nullable: true);
        migrationBuilder.AddColumn<string>("original_end_time_zone", "events", maxLength: 128, nullable: true);
        migrationBuilder.AddColumn<DateOnly>("all_day_start_date", "events", type: "date", nullable: true);
        migrationBuilder.AddColumn<DateOnly>("all_day_end_date_exclusive", "events", type: "date", nullable: true);
        migrationBuilder.AddColumn<string>("graph_recurrence_json", "events", type: "jsonb", nullable: false, defaultValue: "{}");
        migrationBuilder.AddColumn<Guid>("last_seen_sync_generation", "events", nullable: true);
        migrationBuilder.AddColumn<string>("outlook_sync_state", "events", maxLength: 32, nullable: true);

        migrationBuilder.AddColumn<Guid>("source_confirmation_id", "sync_conflicts", nullable: true);
        migrationBuilder.CreateIndex(
            "IX_sync_conflicts_source_confirmation_id",
            "sync_conflicts",
            "source_confirmation_id");
        migrationBuilder.Sql("""
            UPDATE sync_conflicts
            SET source_confirmation_id = resolved_confirmation_id,
                resolved_confirmation_id = NULL
            WHERE provider = 'outlook'
              AND status = 'open'
              AND source_confirmation_id IS NULL
              AND resolved_confirmation_id IS NOT NULL;
            """);

        CreateAuthorizationSessions(migrationBuilder);
        CreateCalendarBindings(migrationBuilder);
        CreateOperationExecutions(migrationBuilder);

        migrationBuilder.CreateIndex(
            "IX_events_outlook_calendar_binding_id_outlook_event_id",
            "events",
            ["outlook_calendar_binding_id", "outlook_event_id"],
            unique: true,
            filter: "\"outlook_calendar_binding_id\" IS NOT NULL AND \"outlook_event_id\" IS NOT NULL AND \"deleted_at\" IS NULL");
        migrationBuilder.CreateIndex(
            "IX_events_outlook_connection_id",
            "events",
            "outlook_connection_id");
        migrationBuilder.AddForeignKey(
            "FK_events_outlook_calendar_bindings_outlook_calendar_binding_id",
            "events",
            "outlook_calendar_binding_id",
            "outlook_calendar_bindings",
            principalColumn: "id",
            onDelete: ReferentialAction.SetNull);
        migrationBuilder.AddForeignKey(
            "FK_events_outlook_connections_outlook_connection_id",
            "events",
            "outlook_connection_id",
            "outlook_connections",
            principalColumn: "id",
            onDelete: ReferentialAction.SetNull);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            "FK_events_outlook_calendar_bindings_outlook_calendar_binding_id",
            "events");
        migrationBuilder.DropForeignKey(
            "FK_events_outlook_connections_outlook_connection_id",
            "events");
        migrationBuilder.DropTable("outlook_operation_executions");
        migrationBuilder.DropTable("outlook_authorization_sessions");
        migrationBuilder.DropTable("outlook_calendar_bindings");
        migrationBuilder.DropIndex("IX_events_outlook_calendar_binding_id_outlook_event_id", "events");
        migrationBuilder.DropIndex("IX_events_outlook_connection_id", "events");
        migrationBuilder.Sql("""
            UPDATE sync_conflicts
            SET resolved_confirmation_id = source_confirmation_id
            WHERE provider = 'outlook'
              AND status = 'open'
              AND resolved_confirmation_id IS NULL
              AND source_confirmation_id IS NOT NULL;
            """);
        migrationBuilder.DropIndex("IX_sync_conflicts_source_confirmation_id", "sync_conflicts");
        migrationBuilder.DropColumn("source_confirmation_id", "sync_conflicts");

        foreach (var column in new[] { "outlook_connection_id", "outlook_calendar_binding_id", "outlook_series_master_id",
                     "outlook_event_type", "original_start_time_zone", "original_end_time_zone", "all_day_start_date",
                     "all_day_end_date_exclusive", "graph_recurrence_json", "last_seen_sync_generation", "outlook_sync_state" })
            migrationBuilder.DropColumn(column, "events");
        foreach (var column in new[] { "source", "is_visible" })
            migrationBuilder.DropColumn(column, "calendars");
        foreach (var column in new[] { "connection_id", "mode", "requested_window_start", "requested_window_end",
                     "requested_calendar_ids_json", "per_calendar_json", "cancel_requested", "updated_at" })
            migrationBuilder.DropColumn(column, "outlook_sync_batches");
        foreach (var column in new[] { "authority", "home_account_id", "account_display_name", "account_login_hint",
                     "msal_cache_encrypted", "version" })
            migrationBuilder.DropColumn(column, "outlook_connections");
    }

    private static void CreateAuthorizationSessions(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "outlook_authorization_sessions",
            columns: table => new
            {
                id = table.Column<Guid>(nullable: false), user_id = table.Column<Guid>(nullable: false),
                connection_id = table.Column<Guid>(nullable: false), status = table.Column<string>(maxLength: 32, nullable: false, defaultValue: "starting"),
                verification_uri = table.Column<string>(maxLength: 512, nullable: true), user_code = table.Column<string>(maxLength: 64, nullable: true),
                expires_at = table.Column<DateTimeOffset>(nullable: true), account_display_name = table.Column<string>(maxLength: 255, nullable: true),
                account_login_hint = table.Column<string>(maxLength: 255, nullable: true), error_code = table.Column<string>(maxLength: 128, nullable: true),
                error_message = table.Column<string>(nullable: true), created_at = table.Column<DateTimeOffset>(nullable: false),
                updated_at = table.Column<DateTimeOffset>(nullable: false)
            }, constraints: constraints => constraints.PrimaryKey("PK_outlook_authorization_sessions", row => row.id));
        migrationBuilder.CreateIndex("IX_outlook_authorization_sessions_user_id_created_at", "outlook_authorization_sessions", ["user_id", "created_at"]);
        migrationBuilder.CreateIndex("IX_outlook_authorization_sessions_connection_id_status", "outlook_authorization_sessions", ["connection_id", "status"]);
    }

    private static void CreateCalendarBindings(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "outlook_calendar_bindings",
            columns: table => new
            {
                id = table.Column<Guid>(nullable: false), connection_id = table.Column<Guid>(nullable: false), pim_calendar_id = table.Column<Guid>(nullable: false),
                graph_calendar_id = table.Column<string>(maxLength: 512, nullable: false), graph_group_id = table.Column<string>(maxLength: 512, nullable: true),
                graph_group_name = table.Column<string>(maxLength: 255, nullable: true), name = table.Column<string>(maxLength: 255, nullable: false),
                color = table.Column<string>(maxLength: 64, nullable: true), owner_name = table.Column<string>(maxLength: 255, nullable: true),
                owner_address = table.Column<string>(maxLength: 320, nullable: true), is_default_calendar = table.Column<bool>(nullable: false),
                can_edit = table.Column<bool>(nullable: false), can_view_private_items = table.Column<bool>(nullable: false), is_selected = table.Column<bool>(nullable: false, defaultValue: true),
                remote_state = table.Column<string>(maxLength: 32, nullable: false, defaultValue: "active"), sync_strategy = table.Column<string>(maxLength: 32, nullable: false, defaultValue: "window-reconcile"),
                delta_link = table.Column<string>(nullable: true), baseline_window_start = table.Column<DateTimeOffset>(nullable: true), baseline_window_end = table.Column<DateTimeOffset>(nullable: true),
                last_full_baseline_at = table.Column<DateTimeOffset>(nullable: true), last_discovery_at = table.Column<DateTimeOffset>(nullable: true), last_synced_at = table.Column<DateTimeOffset>(nullable: true),
                last_successful_generation = table.Column<Guid>(nullable: true), last_error_code = table.Column<string>(maxLength: 128, nullable: true), last_error_message = table.Column<string>(nullable: true),
                created_at = table.Column<DateTimeOffset>(nullable: false), updated_at = table.Column<DateTimeOffset>(nullable: false)
            }, constraints: constraints =>
            {
                constraints.PrimaryKey("PK_outlook_calendar_bindings", row => row.id);
                constraints.ForeignKey("FK_outlook_calendar_bindings_outlook_connections_connection_id", row => row.connection_id, "outlook_connections", "id", onDelete: ReferentialAction.Cascade);
                constraints.ForeignKey("FK_outlook_calendar_bindings_calendars_pim_calendar_id", row => row.pim_calendar_id, "calendars", "id", onDelete: ReferentialAction.Restrict);
            });
        migrationBuilder.CreateIndex("IX_outlook_calendar_bindings_connection_id_graph_calendar_id", "outlook_calendar_bindings", ["connection_id", "graph_calendar_id"], unique: true);
        migrationBuilder.CreateIndex("IX_outlook_calendar_bindings_pim_calendar_id", "outlook_calendar_bindings", "pim_calendar_id", unique: true);
    }

    private static void CreateOperationExecutions(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "outlook_operation_executions",
            columns: table => new
            {
                id = table.Column<Guid>(nullable: false), confirmation_id = table.Column<Guid>(nullable: false), user_id = table.Column<Guid>(nullable: false),
                operation_type = table.Column<string>(maxLength: 128, nullable: false), proposed_hash = table.Column<string>(maxLength: 64, nullable: false),
                payload_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"), state = table.Column<string>(maxLength: 32, nullable: false, defaultValue: "queued"),
                attempt_count = table.Column<int>(nullable: false), next_attempt_at = table.Column<DateTimeOffset>(nullable: true), last_error_code = table.Column<string>(maxLength: 128, nullable: true),
                last_error_message = table.Column<string>(nullable: true), created_at = table.Column<DateTimeOffset>(nullable: false), updated_at = table.Column<DateTimeOffset>(nullable: false),
                completed_at = table.Column<DateTimeOffset>(nullable: true)
            }, constraints: constraints => constraints.PrimaryKey("PK_outlook_operation_executions", row => row.id));
        migrationBuilder.CreateIndex("IX_outlook_operation_executions_confirmation_id", "outlook_operation_executions", "confirmation_id", unique: true);
        migrationBuilder.CreateIndex("IX_outlook_operation_executions_state_next_attempt_at", "outlook_operation_executions", ["state", "next_attempt_at"]);
    }
}
