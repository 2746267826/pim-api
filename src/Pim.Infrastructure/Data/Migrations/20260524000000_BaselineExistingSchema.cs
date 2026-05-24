using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class BaselineExistingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "calendars",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calendars", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outlook_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    access_token_encrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                    refresh_token_encrypted = table.Column<byte[]>(type: "bytea", nullable: true),
                    subscription_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    subscription_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outlook_connections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pc_activity_category_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    category_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    project_tag = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    conditions_json = table.Column<string>(type: "jsonb", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    explanation = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pc_activity_category_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pc_activity_classification_suggestions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cluster_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    sample_count = table.Column<int>(type: "integer", nullable: false),
                    total_duration_seconds = table.Column<double>(type: "double precision", nullable: false),
                    sample_records_json = table.Column<string>(type: "jsonb", nullable: false),
                    sanitized_context_json = table.Column<string>(type: "jsonb", nullable: false),
                    current_category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    suggested_category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    suggested_project_tag = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    suggested_rules_json = table.Column<string>(type: "jsonb", nullable: true),
                    user_feedback = table.Column<string>(type: "text", nullable: true),
                    llm_response_json = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pc_activity_classification_suggestions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pc_app_categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_pattern = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    category_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    is_builtin = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pc_app_categories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pc_aw_buckets",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    pim_device_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    aw_device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    bucket_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    client = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    hostname = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at_source = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_updated_source = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    data_json = table.Column<string>(type: "jsonb", nullable: false),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pc_aw_buckets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pc_aw_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    device_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    duration = table.Column<double>(type: "double precision", nullable: false),
                    event_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    app_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    window_title = table.Column<string>(type: "text", nullable: true),
                    afk_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    aw_device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    aw_hostname = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    bucket_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    bucket_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    bucket_client = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    source_event_id = table.Column<long>(type: "bigint", nullable: true),
                    data_json = table.Column<string>(type: "jsonb", nullable: false),
                    app_name_normalized = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pc_aw_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pc_keystats_daily",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    device_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    snapshot_date = table.Column<DateTime>(type: "date", nullable: false),
                    key_presses = table.Column<int>(type: "integer", nullable: false),
                    left_clicks = table.Column<int>(type: "integer", nullable: false),
                    right_clicks = table.Column<int>(type: "integer", nullable: false),
                    middle_clicks = table.Column<int>(type: "integer", nullable: false),
                    side_back_clicks = table.Column<int>(type: "integer", nullable: false),
                    side_forward_clicks = table.Column<int>(type: "integer", nullable: false),
                    mouse_distance = table.Column<double>(type: "double precision", nullable: false),
                    scroll_distance = table.Column<double>(type: "double precision", nullable: false),
                    peak_kps = table.Column<int>(type: "integer", nullable: false),
                    peak_cps = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pc_keystats_daily", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pc_keystats_samples",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    pim_device_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sampled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    stats_date = table.Column<DateTime>(type: "date", nullable: false),
                    stats_timezone_offset_minutes = table.Column<int>(type: "integer", nullable: false),
                    key_presses = table.Column<int>(type: "integer", nullable: false),
                    left_clicks = table.Column<int>(type: "integer", nullable: false),
                    right_clicks = table.Column<int>(type: "integer", nullable: false),
                    middle_clicks = table.Column<int>(type: "integer", nullable: false),
                    side_back_clicks = table.Column<int>(type: "integer", nullable: false),
                    side_forward_clicks = table.Column<int>(type: "integer", nullable: false),
                    mouse_distance = table.Column<double>(type: "double precision", nullable: false),
                    scroll_distance = table.Column<double>(type: "double precision", nullable: false),
                    peak_kps = table.Column<int>(type: "integer", nullable: false),
                    peak_cps = table.Column<int>(type: "integer", nullable: false),
                    formatted_mouse_distance = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    formatted_scroll_distance = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    key_counts_json = table.Column<string>(type: "jsonb", nullable: false),
                    app_stats_json = table.Column<string>(type: "jsonb", nullable: false),
                    raw_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pc_keystats_samples", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pending_confirmations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_confirmations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scheduling_feedback",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_options = table.Column<string>(type: "jsonb", nullable: false),
                    selected_index = table.Column<int>(type: "integer", nullable: false),
                    context = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduling_feedback", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    calendar_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uid = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    location = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    dtstart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    dtend = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    dtstamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rrule = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    organizer = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    outlook_event_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    schedule_plan_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_events_calendars_calendar_id",
                        column: x => x.calendar_id,
                        principalTable: "calendars",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    calendar_id = table.Column<Guid>(type: "uuid", nullable: true),
                    uid = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    estimated_duration = table.Column<TimeSpan>(type: "interval", nullable: true),
                    minimum_segment = table.Column<TimeSpan>(type: "interval", nullable: true),
                    dtstart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    due = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    percent_complete = table.Column<int>(type: "integer", nullable: false),
                    parent_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_inbox = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    schedule_plan_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tasks", x => x.id);
                    table.ForeignKey(
                        name: "FK_tasks_calendars_calendar_id",
                        column: x => x.calendar_id,
                        principalTable: "calendars",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_tasks_tasks_parent_task_id",
                        column: x => x.parent_task_id,
                        principalTable: "tasks",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "pc_keystats_app_breakdown",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    daily_snapshot_id = table.Column<long>(type: "bigint", nullable: false),
                    app_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    display_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    key_presses = table.Column<int>(type: "integer", nullable: false),
                    left_clicks = table.Column<int>(type: "integer", nullable: false),
                    right_clicks = table.Column<int>(type: "integer", nullable: false),
                    middle_clicks = table.Column<int>(type: "integer", nullable: false),
                    side_back_clicks = table.Column<int>(type: "integer", nullable: false),
                    side_forward_clicks = table.Column<int>(type: "integer", nullable: false),
                    scroll_distance = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pc_keystats_app_breakdown", x => x.id);
                    table.ForeignKey(
                        name: "FK_pc_keystats_app_breakdown_pc_keystats_daily_daily_snapshot_~",
                        column: x => x.daily_snapshot_id,
                        principalTable: "pc_keystats_daily",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pc_keystats_key_counts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    daily_snapshot_id = table.Column<long>(type: "bigint", nullable: false),
                    key_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pc_keystats_key_counts", x => x.id);
                    table.ForeignKey(
                        name: "FK_pc_keystats_key_counts_pc_keystats_daily_daily_snapshot_id",
                        column: x => x.daily_snapshot_id,
                        principalTable: "pc_keystats_daily",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "login_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    success = table.Column<bool>(type: "boolean", nullable: false),
                    attempted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_login_attempts", x => x.id);
                    table.ForeignKey(
                        name: "FK_login_attempts_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_refresh_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_calendars_user_id",
                table: "calendars",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_events_calendar_id",
                table: "events",
                column: "calendar_id");

            migrationBuilder.CreateIndex(
                name: "IX_events_uid",
                table: "events",
                column: "uid");

            migrationBuilder.CreateIndex(
                name: "IX_login_attempts_ip_address_attempted_at",
                table: "login_attempts",
                columns: new[] { "ip_address", "attempted_at" });

            migrationBuilder.CreateIndex(
                name: "IX_login_attempts_user_id",
                table: "login_attempts",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_outlook_connections_user_id",
                table: "outlook_connections",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pc_activity_category_rules_category_name",
                table: "pc_activity_category_rules",
                column: "category_name");

            migrationBuilder.CreateIndex(
                name: "ix_pc_activity_category_rules_priority",
                table: "pc_activity_category_rules",
                column: "priority");

            migrationBuilder.CreateIndex(
                name: "ix_pc_activity_category_rules_project_tag",
                table: "pc_activity_category_rules",
                column: "project_tag");

            migrationBuilder.CreateIndex(
                name: "ix_pc_activity_category_rules_status",
                table: "pc_activity_category_rules",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_pc_activity_category_rules_rule_name",
                table: "pc_activity_category_rules",
                column: "rule_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pc_activity_classification_suggestions_status",
                table: "pc_activity_classification_suggestions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_pc_activity_classification_suggestions_updated_at",
                table: "pc_activity_classification_suggestions",
                column: "updated_at");

            migrationBuilder.CreateIndex(
                name: "ux_pc_activity_classification_suggestions_pending_cluster",
                table: "pc_activity_classification_suggestions",
                column: "cluster_key",
                unique: true,
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "IX_pc_app_categories_category_name",
                table: "pc_app_categories",
                column: "category_name");

            migrationBuilder.CreateIndex(
                name: "IX_pc_app_categories_priority",
                table: "pc_app_categories",
                column: "priority");

            migrationBuilder.CreateIndex(
                name: "ix_pc_aw_buckets_seen_at",
                table: "pc_aw_buckets",
                column: "seen_at");

            migrationBuilder.CreateIndex(
                name: "ix_pc_aw_buckets_type",
                table: "pc_aw_buckets",
                column: "type");

            migrationBuilder.CreateIndex(
                name: "ux_pc_aw_buckets_device_bucket",
                table: "pc_aw_buckets",
                columns: new[] { "pim_device_id", "bucket_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pc_aw_events_app_name_normalized",
                table: "pc_aw_events",
                column: "app_name_normalized");

            migrationBuilder.CreateIndex(
                name: "ix_pc_aw_events_bucket_id",
                table: "pc_aw_events",
                column: "bucket_id");

            migrationBuilder.CreateIndex(
                name: "ix_pc_aw_events_device_id",
                table: "pc_aw_events",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_pc_aw_events_event_type",
                table: "pc_aw_events",
                column: "event_type");

            migrationBuilder.CreateIndex(
                name: "ix_pc_aw_events_source_event_id",
                table: "pc_aw_events",
                column: "source_event_id");

            migrationBuilder.CreateIndex(
                name: "ix_pc_aw_events_timestamp",
                table: "pc_aw_events",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ux_pc_aw_events_source",
                table: "pc_aw_events",
                columns: new[] { "device_id", "bucket_id", "source_event_id" },
                unique: true,
                filter: "bucket_id IS NOT NULL AND source_event_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_pc_keystats_app_breakdown_daily_snapshot_id",
                table: "pc_keystats_app_breakdown",
                column: "daily_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_pc_keystats_daily_device_id",
                table: "pc_keystats_daily",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_pc_keystats_daily_device_id_snapshot_date",
                table: "pc_keystats_daily",
                columns: new[] { "device_id", "snapshot_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pc_keystats_daily_snapshot_date",
                table: "pc_keystats_daily",
                column: "snapshot_date");

            migrationBuilder.CreateIndex(
                name: "IX_pc_keystats_key_counts_daily_snapshot_id",
                table: "pc_keystats_key_counts",
                column: "daily_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "ix_pc_keystats_samples_stats_date",
                table: "pc_keystats_samples",
                column: "stats_date");

            migrationBuilder.CreateIndex(
                name: "ux_pc_keystats_samples_device_minute",
                table: "pc_keystats_samples",
                columns: new[] { "pim_device_id", "sampled_at_utc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pending_confirmations_status",
                table: "pending_confirmations",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_pending_confirmations_user_id",
                table: "pending_confirmations",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_user_id",
                table: "refresh_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_scheduling_feedback_user_id",
                table: "scheduling_feedback",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_calendar_id",
                table: "tasks",
                column: "calendar_id");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_parent_task_id",
                table: "tasks",
                column: "parent_task_id");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_status",
                table: "tasks",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_user_id",
                table: "tasks",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_user_id_calendar_id",
                table: "tasks",
                columns: new[] { "user_id", "calendar_id" });

            migrationBuilder.CreateIndex(
                name: "IX_users_email",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_username",
                table: "users",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "login_attempts");

            migrationBuilder.DropTable(
                name: "outlook_connections");

            migrationBuilder.DropTable(
                name: "pc_activity_category_rules");

            migrationBuilder.DropTable(
                name: "pc_activity_classification_suggestions");

            migrationBuilder.DropTable(
                name: "pc_app_categories");

            migrationBuilder.DropTable(
                name: "pc_aw_buckets");

            migrationBuilder.DropTable(
                name: "pc_aw_events");

            migrationBuilder.DropTable(
                name: "pc_keystats_app_breakdown");

            migrationBuilder.DropTable(
                name: "pc_keystats_key_counts");

            migrationBuilder.DropTable(
                name: "pc_keystats_samples");

            migrationBuilder.DropTable(
                name: "pending_confirmations");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "scheduling_feedback");

            migrationBuilder.DropTable(
                name: "tasks");

            migrationBuilder.DropTable(
                name: "pc_keystats_daily");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "calendars");
        }
    }
}
