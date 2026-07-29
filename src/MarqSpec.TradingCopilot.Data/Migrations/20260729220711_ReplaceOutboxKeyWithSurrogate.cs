using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceOutboxKeyWithSurrogate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_NotificationOutbox",
                table: "NotificationOutbox");

            migrationBuilder.AddColumn<Guid>(
                name: "Id",
                table: "NotificationOutbox",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Every pre-existing row would otherwise share the zero Guid and collide on the new primary key. The
            // table is empty on every real deployment -- gh#400 part 1 shipped it with nothing registered to write
            // to it -- but a migration that is only correct while an assumption holds is a migration that breaks
            // the one time it does not.
            migrationBuilder.Sql("""UPDATE "NotificationOutbox" SET "Id" = gen_random_uuid() WHERE "Id" = '00000000-0000-0000-0000-000000000000';""");

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
