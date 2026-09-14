using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRelevanceVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_News_RelevanceResolvedAt",
                table: "News");

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "RelevanceConfigStates",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Nullable with no backfill (gh#418): every existing row becomes RelevanceVersion = null == "never
            // resolved against a versioned config", so the first post-deploy pass re-resolves all news exactly
            // once against generation 0. That one-time pass is deliberate and safe -- it is idempotent, bounded
            // by the existing paging, and it guarantees no row that was stale under the old wall-clock scheme can
            // survive the transition marked fresh. Rows drop out of the predicate as they resolve.
            migrationBuilder.AddColumn<long>(
                name: "RelevanceVersion",
                table: "News",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_News_RelevanceVersion",
                table: "News",
                column: "RelevanceVersion");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_News_RelevanceVersion",
                table: "News");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "RelevanceConfigStates");

            migrationBuilder.DropColumn(
                name: "RelevanceVersion",
                table: "News");

            migrationBuilder.CreateIndex(
                name: "IX_News_RelevanceResolvedAt",
                table: "News",
                column: "RelevanceResolvedAt");
        }
    }
}
