using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGateDecisionsAndStartingBalance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "StartingBalance",
                table: "RiskProfiles",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "GateDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    ApprovedQuantity = table.Column<int>(type: "integer", nullable: false),
                    BindingLayer = table.Column<int>(type: "integer", nullable: true),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GateDecisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GateDecisions_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GateDecisions_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_RiskProfiles_StartingBalance_Positive",
                table: "RiskProfiles",
                sql: "\"StartingBalance\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_GateDecisions_AccountId_DecidedAt",
                table: "GateDecisions",
                columns: new[] { "AccountId", "DecidedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GateDecisions_OrderId",
                table: "GateDecisions",
                column: "OrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GateDecisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RiskProfiles_StartingBalance_Positive",
                table: "RiskProfiles");

            migrationBuilder.DropColumn(
                name: "StartingBalance",
                table: "RiskProfiles");
        }
    }
}
