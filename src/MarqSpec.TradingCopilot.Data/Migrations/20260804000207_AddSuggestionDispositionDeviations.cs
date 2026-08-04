using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSuggestionDispositionDeviations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Deviations",
                table: "SuggestionDispositions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "TakenEntryPrice",
                table: "SuggestionDispositions",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TakenSize",
                table: "SuggestionDispositions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TakenStopPrice",
                table: "SuggestionDispositions",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TakenTargetPrice",
                table: "SuggestionDispositions",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SuggestionDispositions_Deviations_MatchModified",
                table: "SuggestionDispositions",
                sql: "(\"Kind\" = 2) = (\"Deviations\" <> 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SuggestionDispositions_TakeSnapshot",
                table: "SuggestionDispositions",
                sql: "(\"Kind\" IN (1, 2) AND \"TakenEntryPrice\" IS NOT NULL AND \"TakenStopPrice\" IS NOT NULL AND \"TakenSize\" IS NOT NULL) OR (\"Kind\" = 3 AND \"TakenEntryPrice\" IS NULL AND \"TakenStopPrice\" IS NULL AND \"TakenTargetPrice\" IS NULL AND \"TakenSize\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SuggestionDispositions_Deviations_MatchModified",
                table: "SuggestionDispositions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SuggestionDispositions_TakeSnapshot",
                table: "SuggestionDispositions");

            migrationBuilder.DropColumn(
                name: "Deviations",
                table: "SuggestionDispositions");

            migrationBuilder.DropColumn(
                name: "TakenEntryPrice",
                table: "SuggestionDispositions");

            migrationBuilder.DropColumn(
                name: "TakenSize",
                table: "SuggestionDispositions");

            migrationBuilder.DropColumn(
                name: "TakenStopPrice",
                table: "SuggestionDispositions");

            migrationBuilder.DropColumn(
                name: "TakenTargetPrice",
                table: "SuggestionDispositions");
        }
    }
}
