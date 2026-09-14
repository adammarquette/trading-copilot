using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRuleEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Enabled / Confirmed / NeedsRevalidation have no column default: the C# CLR false is the inert
            // fail-closed (gh#866). A standing SQL default would let a writer that forgot the flags look live.
            migrationBuilder.CreateTable(
                name: "Rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IntentText = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    StructuredForm = table.Column<string>(type: "jsonb", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    SourceConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    InstrumentDependencySnapshot = table.Column<string>(type: "jsonb", nullable: true),
                    NeedsRevalidation = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Rules_UserId_Confirmed_Enabled",
                table: "Rules",
                columns: new[] { "UserId", "Confirmed", "Enabled" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Rules");
        }
    }
}
