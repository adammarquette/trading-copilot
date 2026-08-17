using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOutcomeSuppression : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutcomeSuppressions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TradeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SuggestionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutcomeSuppressions", x => x.Id);
                    table.CheckConstraint("CK_OutcomeSuppressions_OneParent", "num_nonnulls(\"TradeId\", \"SuggestionId\") = 1");
                    table.ForeignKey(
                        name: "FK_OutcomeSuppressions_Suggestions_SuggestionId",
                        column: x => x.SuggestionId,
                        principalTable: "Suggestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OutcomeSuppressions_Trades_TradeId",
                        column: x => x.TradeId,
                        principalTable: "Trades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutcomeSuppressions_SuggestionId",
                table: "OutcomeSuppressions",
                column: "SuggestionId",
                unique: true,
                filter: "\"SuggestionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutcomeSuppressions_TradeId",
                table: "OutcomeSuppressions",
                column: "TradeId",
                unique: true,
                filter: "\"TradeId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutcomeSuppressions");
        }
    }
}
