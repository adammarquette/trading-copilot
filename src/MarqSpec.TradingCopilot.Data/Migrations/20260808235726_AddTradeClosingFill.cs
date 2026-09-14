using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTradeClosingFill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Trades_AccountId",
                table: "Trades");

            migrationBuilder.AddColumn<Guid>(
                name: "ClosingFillId",
                table: "Trades",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Trades_AccountId_ClosedAt",
                table: "Trades",
                columns: new[] { "AccountId", "ClosedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Trades_ClosingFillId",
                table: "Trades",
                column: "ClosingFillId",
                unique: true,
                filter: "\"ClosingFillId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Trades_Fills_ClosingFillId",
                table: "Trades",
                column: "ClosingFillId",
                principalTable: "Fills",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Trades_Fills_ClosingFillId",
                table: "Trades");

            migrationBuilder.DropIndex(
                name: "IX_Trades_AccountId_ClosedAt",
                table: "Trades");

            migrationBuilder.DropIndex(
                name: "IX_Trades_ClosingFillId",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "ClosingFillId",
                table: "Trades");

            migrationBuilder.CreateIndex(
                name: "IX_Trades_AccountId",
                table: "Trades",
                column: "AccountId");
        }
    }
}
