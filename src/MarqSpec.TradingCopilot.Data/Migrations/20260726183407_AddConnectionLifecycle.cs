using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue TRUE, not EF's generated false. These columns are added to a live workspace, and a
            // false backfill would mark every EXISTING connection and account INACTIVE on upgrade -- silently
            // deactivating the operator's whole setup, with the only symptom being that nothing works any more.
            // Active is also the right ongoing default: a row is live unless deliberately deactivated (gh#210).
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Connections",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Accounts",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Accounts");
        }
    }
}
