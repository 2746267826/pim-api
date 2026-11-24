using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteTaskSuggestionEventsAndIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "end_at",
                table: "note_task_suggestions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "event_id",
                table: "note_task_suggestions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "item_fingerprint",
                table: "note_task_suggestions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "note_task_suggestions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Task");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "start_at",
                table: "note_task_suggestions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "time_zone_id",
                table: "note_task_suggestions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            // Existing confirmed suggestions predate the durable item fingerprint. Backfill
            // only the earliest row per stable item identity so the new unique index can be
            // created even if a note already contains historical duplicates.
            migrationBuilder.Sql("""
                WITH candidate_rows AS (
                    SELECT
                        id,
                        concat_ws('|',
                            'v3',
                            encode(convert_to(user_id::text, 'UTF8'), 'hex'),
                            encode(convert_to(note_id::text, 'UTF8'), 'hex'),
                            encode(convert_to(COALESCE(kind, 'Task'), 'UTF8'), 'hex'),
                            encode(convert_to(btrim(title), 'UTF8'), 'hex'),
                            CASE
                                WHEN description IS NULL THEN '-'
                                ELSE encode(convert_to(btrim(description), 'UTF8'), 'hex')
                            END,
                            priority::text,
                            COALESCE(floor(extract(epoch FROM due))::bigint::text, '-'),
                            COALESCE(floor(extract(epoch FROM start_at))::bigint::text, '-'),
                            COALESCE(floor(extract(epoch FROM end_at))::bigint::text, '-'),
                            CASE
                                WHEN time_zone_id IS NULL THEN '-'
                                ELSE encode(convert_to(btrim(time_zone_id), 'UTF8'), 'hex')
                            END,
                            CASE
                                WHEN task_book_id IS NULL THEN '-'
                                ELSE encode(convert_to(task_book_id::text, 'UTF8'), 'hex')
                            END,
                            CASE
                                WHEN calendar_id IS NULL THEN '-'
                                ELSE encode(convert_to(calendar_id::text, 'UTF8'), 'hex')
                            END
                        ) AS payload,
                        confirmed_at,
                        created_at
                    FROM note_task_suggestions
                    WHERE status = 'Confirmed'
                ),
                fingerprint_rows AS (
                    SELECT
                        id,
                        payload,
                        ROW_NUMBER() OVER (
                            PARTITION BY payload
                            ORDER BY confirmed_at NULLS LAST, created_at, id
                        ) AS ordinal
                    FROM candidate_rows
                )
                UPDATE note_task_suggestions AS target
                SET item_fingerprint = encode(sha256(convert_to(rows.payload, 'UTF8')), 'hex')
                FROM fingerprint_rows AS rows
                WHERE target.id = rows.id AND rows.ordinal = 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_note_task_suggestions_event_id",
                table: "note_task_suggestions",
                column: "event_id",
                unique: true,
                filter: "\"event_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_note_task_suggestions_note_fingerprint_confirmed",
                table: "note_task_suggestions",
                columns: new[] { "user_id", "note_id", "item_fingerprint" },
                unique: true,
                filter: "\"status\" = 'Confirmed' AND \"item_fingerprint\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_note_task_suggestions_event_id",
                table: "note_task_suggestions");

            migrationBuilder.DropIndex(
                name: "IX_note_task_suggestions_note_fingerprint_confirmed",
                table: "note_task_suggestions");

            migrationBuilder.DropColumn(
                name: "end_at",
                table: "note_task_suggestions");

            migrationBuilder.DropColumn(
                name: "event_id",
                table: "note_task_suggestions");

            migrationBuilder.DropColumn(
                name: "item_fingerprint",
                table: "note_task_suggestions");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "note_task_suggestions");

            migrationBuilder.DropColumn(
                name: "start_at",
                table: "note_task_suggestions");

            migrationBuilder.DropColumn(
                name: "time_zone_id",
                table: "note_task_suggestions");
        }
    }
}
