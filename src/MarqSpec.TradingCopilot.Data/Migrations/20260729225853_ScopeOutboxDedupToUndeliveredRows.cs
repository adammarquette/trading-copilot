using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScopeOutboxDedupToUndeliveredRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_NotificationOutbox",
                table: "NotificationOutbox");

            // A VOLATILE default, so every pre-existing row gets a DISTINCT id. The scaffolded default was
            // Guid.Empty for all of them, which AddPrimaryKey below would then reject as duplicate -- a migration
            // that passes on an empty dev database and fails on the one deployment that has alert history.
            migrationBuilder.AddColumn<Guid>(
                name: "Id",
                table: "NotificationOutbox",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            // ...and then drop it: ids come from the application (Guid.CreateVersion7, time-ordered), so a
            // lingering database default would be schema the model does not declare.
            migrationBuilder.Sql("ALTER TABLE \"NotificationOutbox\" ALTER COLUMN \"Id\" DROP DEFAULT;");

            migrationBuilder.AddPrimaryKey(
                name: "PK_NotificationOutbox",
                table: "NotificationOutbox",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_DedupKey",
                table: "NotificationOutbox",
                column: "DedupKey",
                unique: true,
                filter: "\"DeliveredAt\" IS NULL");
        }

        /// <inheritdoc />
        /// <remarks>
        /// Reverting restores the defect, and it can legitimately FAIL: once the outbox holds what the fix permits
        /// -- a redelivered incident alongside its delivered history -- DedupKey is no longer unique, so
        /// AddPrimaryKey below rejects it. That is the right failure. The alternative is a Down that deletes the
        /// operator's alert history to make room for the old key, and silently discarding delivered pages to roll
        /// back a schema change is worse than refusing to roll back.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_NotificationOutbox",
                table: "NotificationOutbox");

            migrationBuilder.DropIndex(
                name: "IX_NotificationOutbox_DedupKey",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "NotificationOutbox");

            migrationBuilder.AddPrimaryKey(
                name: "PK_NotificationOutbox",
                table: "NotificationOutbox",
                column: "DedupKey");
        }
    }
}
