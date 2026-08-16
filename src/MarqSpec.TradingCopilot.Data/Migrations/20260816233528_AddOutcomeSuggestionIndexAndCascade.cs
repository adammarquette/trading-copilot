using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOutcomeSuggestionIndexAndCascade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Outcomes_Suggestions_SuggestionId",
                table: "Outcomes");

            migrationBuilder.DropIndex(
                name: "IX_Outcomes_SuggestionId",
                table: "Outcomes");

            migrationBuilder.CreateIndex(
                name: "IX_Outcomes_SuggestionId",
                table: "Outcomes",
                column: "SuggestionId",
                unique: true,
                filter: "\"SuggestionId\" IS NOT NULL AND \"TradeId\" IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Outcomes_Suggestions_SuggestionId",
                table: "Outcomes",
                column: "SuggestionId",
                principalTable: "Suggestions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Outcomes_Suggestions_SuggestionId",
                table: "Outcomes");

            migrationBuilder.DropIndex(
                name: "IX_Outcomes_SuggestionId",
                table: "Outcomes");

            migrationBuilder.CreateIndex(
                name: "IX_Outcomes_SuggestionId",
                table: "Outcomes",
                column: "SuggestionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Outcomes_Suggestions_SuggestionId",
                table: "Outcomes",
                column: "SuggestionId",
                principalTable: "Suggestions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
