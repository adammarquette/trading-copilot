using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConditionalOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConditionalOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    SuggestionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Instrument = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Side = table.Column<int>(type: "integer", nullable: false),
                    Size = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    WorkingStopPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    SafetyStopPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    ReferencePrice = table.Column<decimal>(type: "numeric", nullable: false),
                    TickSize = table.Column<decimal>(type: "numeric", nullable: false),
                    PointValue = table.Column<decimal>(type: "numeric", nullable: false),
                    TakeProfitPrice = table.Column<decimal>(type: "numeric", nullable: true),
                    TriggerPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    TriggerDirection = table.Column<int>(type: "integer", nullable: false),
                    CancelDriftPrice = table.Column<decimal>(type: "numeric", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    FiredOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConditionalOrders", x => x.Id);
                    table.CheckConstraint("CK_ConditionalOrders_CancelDrift_StaleSide", "\"CancelDriftPrice\" IS NULL OR (\"TriggerDirection\" = 1 AND \"CancelDriftPrice\" < \"TriggerPrice\") OR (\"TriggerDirection\" = 2 AND \"CancelDriftPrice\" > \"TriggerPrice\")");
                    table.CheckConstraint("CK_ConditionalOrders_Direction_NotUnknown", "\"TriggerDirection\" <> 0");
                    table.CheckConstraint("CK_ConditionalOrders_Mode_NotUndeclared", "\"Mode\" <> 0");
                    table.CheckConstraint("CK_ConditionalOrders_Size_Positive", "\"Size\" > 0");
                    table.CheckConstraint("CK_ConditionalOrders_Status_NotUnknown", "\"Status\" <> 0");
                    table.ForeignKey(
                        name: "FK_ConditionalOrders_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConditionalOrders_Orders_FiredOrderId",
                        column: x => x.FiredOrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ConditionalOrders_Suggestions_SuggestionId",
                        column: x => x.SuggestionId,
                        principalTable: "Suggestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConditionalOrders_AccountId",
                table: "ConditionalOrders",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ConditionalOrders_FiredOrderId",
                table: "ConditionalOrders",
                column: "FiredOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ConditionalOrders_Status",
                table: "ConditionalOrders",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ConditionalOrders_SuggestionId",
                table: "ConditionalOrders",
                column: "SuggestionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConditionalOrders");
        }
    }
}
