using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderEntryMethod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EntryMethod",
                table: "Orders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Orders_EntryMethod_NotUnknown",
                table: "Orders",
                sql: "\"EntryMethod\" IS NULL OR \"EntryMethod\" <> 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Orders_EntryMethod_NotUnknown",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "EntryMethod",
                table: "Orders");
        }
    }
}
