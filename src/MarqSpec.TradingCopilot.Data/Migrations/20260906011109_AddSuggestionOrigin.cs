using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSuggestionOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // BACKFILL TO Scan (1), NOT to the refusable zero. Until gh#1134 the trigger scan was the only writer of
            // a Suggestion row, so every pre-existing row genuinely IS scan-issued -- and a 0 backfill would leave
            // history violating the CHECK added below the moment it is created.
            migrationBuilder.AddColumn<int>(
                name: "Origin",
                table: "Suggestions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            // Then DROP the column default, so it exists only for the backfill above. A standing default would let a
            // future writer that forgot to state its producer be silently recorded as the scan's -- exactly the
            // inference this column was added to replace. With no default, the C# `required` member is the
            // compile-time guard and this CHECK is the runtime one.
            migrationBuilder.Sql("ALTER TABLE \"Suggestions\" ALTER COLUMN \"Origin\" DROP DEFAULT;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Suggestions_Origin_NotUnknown",
                table: "Suggestions",
                sql: "\"Origin\" <> 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Suggestions_Origin_NotUnknown",
                table: "Suggestions");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "Suggestions");
        }
    }
}
