using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Outcomes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TradeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SuggestionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Resolution = table.Column<int>(type: "integer", nullable: false),
                    Simulated = table.Column<bool>(type: "boolean", nullable: false),
                    PredictedRewardRisk = table.Column<decimal>(type: "numeric", nullable: true),
                    RealizedRewardRisk = table.Column<decimal>(type: "numeric", nullable: true),
                    TrainingExcluded = table.Column<bool>(type: "boolean", nullable: false),
                    HiddenFromUser = table.Column<bool>(type: "boolean", nullable: false),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outcomes", x => x.Id);
                    table.CheckConstraint("CK_Outcomes_ParentPresent", "\"TradeId\" IS NOT NULL OR \"SuggestionId\" IS NOT NULL");
                    table.CheckConstraint("CK_Outcomes_Resolution_NotUnknown", "\"Resolution\" <> 0");
                    table.ForeignKey(
                        name: "FK_Outcomes_Suggestions_SuggestionId",
                        column: x => x.SuggestionId,
                        principalTable: "Suggestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Outcomes_Trades_TradeId",
                        column: x => x.TradeId,
                        principalTable: "Trades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Outcomes_SuggestionId",
                table: "Outcomes",
                column: "SuggestionId");

            migrationBuilder.CreateIndex(
                name: "IX_Outcomes_TradeId",
                table: "Outcomes",
                column: "TradeId",
                unique: true,
                filter: "\"TradeId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Outcomes");
        }
    }
}
