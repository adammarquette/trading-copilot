using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStopPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StopPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Side = table.Column<int>(type: "integer", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    ActualStopPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    SafetyStopPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    ProximityMetric = table.Column<int>(type: "integer", nullable: false),
                    ProximityValue = table.Column<decimal>(type: "numeric", nullable: false),
                    Staging = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StopPlans", x => x.Id);
                    table.CheckConstraint("CK_StopPlans_ProximityMetric_NotUnknown", "\"ProximityMetric\" <> 0");
                    table.CheckConstraint("CK_StopPlans_ProximityValue_Positive", "\"ProximityValue\" > 0");
                    table.CheckConstraint("CK_StopPlans_SafetyBeyondActual", "(\"Side\" = 0 AND \"SafetyStopPrice\" < \"ActualStopPrice\" AND \"ActualStopPrice\" < \"EntryPrice\") OR (\"Side\" = 1 AND \"SafetyStopPrice\" > \"ActualStopPrice\" AND \"ActualStopPrice\" > \"EntryPrice\")");
                    table.CheckConstraint("CK_StopPlans_Staging_NotUnknown", "\"Staging\" <> 0");
                    table.ForeignKey(
                        name: "FK_StopPlans_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StopPlans_OrderId",
                table: "StopPlans",
                column: "OrderId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StopPlans");
        }
    }
}
