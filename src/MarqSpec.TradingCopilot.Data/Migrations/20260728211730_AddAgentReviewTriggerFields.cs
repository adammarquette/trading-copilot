using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentReviewTriggerFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AccountId",
                table: "Triggers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Size",
                table: "Triggers",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Triggers_AccountId",
                table: "Triggers",
                column: "AccountId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Triggers_AgentReview_RequiresAccountAndSize",
                table: "Triggers",
                sql: "\"Route\" <> 2 OR (\"AccountId\" IS NOT NULL AND \"Size\" > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Triggers_Mechanical_NoAccount",
                table: "Triggers",
                sql: "\"Route\" = 2 OR (\"AccountId\" IS NULL AND \"Size\" IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_Triggers_Accounts_AccountId",
                table: "Triggers",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Triggers_Accounts_AccountId",
                table: "Triggers");

            migrationBuilder.DropIndex(
                name: "IX_Triggers_AccountId",
                table: "Triggers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Triggers_AgentReview_RequiresAccountAndSize",
                table: "Triggers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Triggers_Mechanical_NoAccount",
                table: "Triggers");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Triggers");

            migrationBuilder.DropColumn(
                name: "Size",
                table: "Triggers");
        }
    }
}
