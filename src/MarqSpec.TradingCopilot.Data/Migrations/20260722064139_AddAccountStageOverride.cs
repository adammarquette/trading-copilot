using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountStageOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StageOverride",
                table: "Accounts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Accounts_StageOverride_NotUnknown",
                table: "Accounts",
                sql: "\"StageOverride\" <> 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Accounts_StageOverride_NotUnknown",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "StageOverride",
                table: "Accounts");
        }
    }
}
