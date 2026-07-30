using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTriggerConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Triggers_Enabled_Route",
                table: "Triggers");

            // defaultValue 0 == TriggerConfirmation.Unconfirmed, and it is load-bearing: it is the fail-closed default
            // for every FUTURE insert that omits the column (gh#470). But it also fills EXISTING rows as 0, and every
            // trigger already in service was authored under the old world where creation armed it -- an operator is
            // relying on those firing. Backfilling them to Unconfirmed would silently disarm live alerts, a schema
            // change changing behaviour (the gh#380 lesson, inverted: there the safe value WAS the zero, here it is
            // not). So restore them to Confirmed explicitly. Divergent targets -- new rows fail-closed, existing rows
            // preserved -- are exactly why this needs an UPDATE rather than a single defaultValue like gh#380 could use.
            migrationBuilder.AddColumn<int>(
                name: "Confirmation",
                table: "Triggers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("UPDATE \"Triggers\" SET \"Confirmation\" = 1;");

            migrationBuilder.CreateIndex(
                name: "IX_Triggers_Confirmation_Enabled_Route",
                table: "Triggers",
                columns: new[] { "Confirmation", "Enabled", "Route" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Triggers_Confirmation_Known",
                table: "Triggers",
                sql: "\"Confirmation\" IN (0, 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Triggers_Confirmation_Enabled_Route",
                table: "Triggers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Triggers_Confirmation_Known",
                table: "Triggers");

            migrationBuilder.DropColumn(
                name: "Confirmation",
                table: "Triggers");

            migrationBuilder.CreateIndex(
                name: "IX_Triggers_Enabled_Route",
                table: "Triggers",
                columns: new[] { "Enabled", "Route" });
        }
    }
}
