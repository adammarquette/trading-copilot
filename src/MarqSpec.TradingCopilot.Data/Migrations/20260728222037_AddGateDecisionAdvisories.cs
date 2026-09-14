using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGateDecisionAdvisories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NULLABLE with no default, deliberately (gh#407). Null means "the gate raised no advisory", which is
            // exactly what every row written before this column existed means -- so the backfill is a no-op and old
            // and new rows read identically. An empty-array default would instead claim that historical decisions
            // were positively known to carry no warning, which is not something this migration can know.
            //
            // jsonb rather than a child table: an advisory is a detail OF one decision, read back with it and never
            // queried alone. Same shape the event log already uses for Event.Payload.
            migrationBuilder.AddColumn<string>(
                name: "Advisories",
                table: "GateDecisions",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Advisories",
                table: "GateDecisions");
        }
    }
}
