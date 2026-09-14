using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPositionActionIntent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PositionActionIntents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<int>(type: "integer", nullable: false),
                    VenueAccountKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Contract = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RequestedQuantity = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PositionActionIntents", x => x.Id);
                    table.CheckConstraint("CK_PositionActionIntents_Action_NotUnknown", "\"Action\" <> 0");
                    table.CheckConstraint("CK_PositionActionIntents_Status_NotUnknown", "\"Status\" <> 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PositionActionIntents_Status",
                table: "PositionActionIntents",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PositionActionIntents_UserId_CreatedAt",
                table: "PositionActionIntents",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PositionActionIntents");
        }
    }
}
