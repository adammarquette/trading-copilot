using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTriggerUnmeasurableSince : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NULLABLE with no default (gh#469). Null means "the last reading was measurable", so every existing
            // trigger starts clean rather than being back-dated into an outage it may never have had -- a schema
            // change must not manufacture a warning about history it cannot know. A genuinely unevaluable trigger
            // starts its clock on the next scan and reports 30 minutes later, which is the correct behaviour for a
            // brand-new column: it observes rather than asserts.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UnmeasurableSince",
                table: "Triggers",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UnmeasurableSince",
                table: "Triggers");
        }
    }
}
