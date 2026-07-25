using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderTakeProfit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "TakeProfitPrice",
                table: "Orders",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Orders_TakeProfit_WinningSide",
                table: "Orders",
                sql: "\"TakeProfitPrice\" IS NULL OR (\"Side\" = 0 AND \"TakeProfitPrice\" > \"EntryPrice\") OR (\"Side\" = 1 AND \"TakeProfitPrice\" < \"EntryPrice\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Orders_TakeProfit_WinningSide",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TakeProfitPrice",
                table: "Orders");
        }
    }
}
