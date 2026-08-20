using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTriggerThresholdCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOT VALID: enforce the rule on every NEW insert / update — the backstop below any non-API writer
            // (gh#1007) — WITHOUT failing this migration on a trigger row authored before the gate existed. A
            // permanently-satisfied trigger (a threshold at or beyond its indicator's range) is exactly the pre-fix
            // data this might meet, and a failed migration would block the whole deploy, the safety-critical hosts
            // included. Such a row survives until it is next written, at which point Postgres re-checks the whole new
            // row against the constraint — so the operator repairs it by PATCHing it to a VALID threshold, or by
            // deleting it; a partial patch that leaves the out-of-range threshold in place is itself refused. (EF's
            // AddCheckConstraint would emit a VALIDATED constraint that checks existing rows, hence the raw SQL here.)
            migrationBuilder.Sql(
                "ALTER TABLE \"Triggers\" ADD CONSTRAINT \"CK_Triggers_Threshold_InIndicatorRange\" "
                + "CHECK ((\"Indicator\" = 'rsi' AND \"Threshold\" > 0 AND \"Threshold\" < 100) "
                + "OR (\"Indicator\" = 'atr' AND \"Threshold\" > 0)) NOT VALID;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Triggers_Threshold_InIndicatorRange",
                table: "Triggers");
        }
    }
}
