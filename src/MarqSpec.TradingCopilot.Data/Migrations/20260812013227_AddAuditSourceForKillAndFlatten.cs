using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditSourceForKillAndFlatten : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "AuditRecords",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AuditRecords_Source_NotUnknown",
                table: "AuditRecords",
                sql: "\"Source\" IS NULL OR \"Source\" <> 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AuditRecords_Source_MatchesAction",
                table: "AuditRecords",
                sql: "(\"Action\" IN (5, 6, 7)) = (\"Source\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AuditRecords_Source_MatchesAction",
                table: "AuditRecords");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AuditRecords_Source_NotUnknown",
                table: "AuditRecords");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "AuditRecords");
        }
    }
}
