using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrimaryOperatorFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPrimaryOperator",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfill (raw SQL on purpose): the primary operator on an existing deployment is the bootstrap-
            // seeded account -- exactly the user(s) NOT created through an accepted invitation. Without this,
            // the operator would be locked out of issuing invitations by their own gh#128 fix.
            migrationBuilder.Sql("""
                UPDATE "Users" SET "IsPrimaryOperator" = TRUE
                WHERE "Id" NOT IN (
                    SELECT "AcceptedByUserId" FROM "Invitations" WHERE "AcceptedByUserId" IS NOT NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPrimaryOperator",
                table: "Users");
        }
    }
}
