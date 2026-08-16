using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiUsageTriggerFiringId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TriggerFiringId",
                table: "AiUsage",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiUsage_UserId_TriggerFiringId",
                table: "AiUsage",
                columns: new[] { "UserId", "TriggerFiringId" },
                filter: "\"TriggerFiringId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AiUsage_UserId_TriggerFiringId",
                table: "AiUsage");

            migrationBuilder.DropColumn(
                name: "TriggerFiringId",
                table: "AiUsage");
        }
    }
}
