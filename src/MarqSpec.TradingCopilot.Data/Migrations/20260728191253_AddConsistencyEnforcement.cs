using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConsistencyEnforcement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue 0 == ConsistencyEnforcement.Advisory, and it is load-bearing rather than incidental
            // (gh#380). Every existing profile has been storing a MaxBestDayFraction that nothing enforced,
            // because the column was never mapped through to the gate. Backfilling those rows as Block would
            // make this migration start REFUSING ORDERS on accounts whose operator never asked for that -- a
            // schema change silently changing trading behaviour. Advisory preserves what those accounts do today
            // and leaves turning the refusal on as an explicit act.
            migrationBuilder.AddColumn<int>(
                name: "ConsistencyEnforcement",
                table: "RiskProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsistencyEnforcement",
                table: "RiskProfiles");
        }
    }
}
