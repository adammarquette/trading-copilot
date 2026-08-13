using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTradeOpeningFillKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Trades_ClosingFillId",
                table: "Trades");

            migrationBuilder.AddColumn<Guid>(
                name: "OpeningFillId",
                table: "Trades",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Trades_ClosingFillId_OpeningFillId",
                table: "Trades",
                columns: new[] { "ClosingFillId", "OpeningFillId" },
                unique: true,
                filter: "\"ClosingFillId\" IS NOT NULL AND \"OpeningFillId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Trades_OpeningFillId",
                table: "Trades",
                column: "OpeningFillId");

            migrationBuilder.AddForeignKey(
                name: "FK_Trades_Fills_OpeningFillId",
                table: "Trades",
                column: "OpeningFillId",
                principalTable: "Fills",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Trades_Fills_OpeningFillId",
                table: "Trades");

            migrationBuilder.DropIndex(
                name: "IX_Trades_ClosingFillId_OpeningFillId",
                table: "Trades");

            migrationBuilder.DropIndex(
                name: "IX_Trades_OpeningFillId",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "OpeningFillId",
                table: "Trades");

            // Re-narrowing to a single-column unique index can legitimately FAIL on real data (gh#759, ADR-0022): once
            // a spanning exit has written two legs that share one ClosingFillId, that column is no longer unique, so
            // this recreate raises a duplicate-key error -- the same documented hazard as the outbox-dedup narrowing
            // (20260729225853). A rollback past this migration must first reconcile or drop those rows.
            migrationBuilder.CreateIndex(
                name: "IX_Trades_ClosingFillId",
                table: "Trades",
                column: "ClosingFillId",
                unique: true,
                filter: "\"ClosingFillId\" IS NOT NULL");
        }
    }
}
