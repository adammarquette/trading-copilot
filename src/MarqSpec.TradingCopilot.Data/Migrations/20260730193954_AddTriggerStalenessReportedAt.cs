using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTriggerStalenessReportedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NULLABLE with no default (gh#515). Null means "this outage has not been reported", which is the
            // correct starting point for every existing row: a trigger mid-outage at upgrade time gets exactly one
            // warning on the next pass and then falls silent, rather than either being back-dated into silence
            // about a live problem or continuing the ~1,440-a-day noise the column exists to stop.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StalenessReportedAt",
                table: "Triggers",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StalenessReportedAt",
                table: "Triggers");
        }
    }
}
