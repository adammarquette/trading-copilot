using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftSignalDirectionAxis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SoftSignalFeedbacks_UserId_NewsDedupKey",
                table: "SoftSignalFeedbacks");

            migrationBuilder.CreateIndex(
                name: "UX_SoftSignalFeedback_Direction",
                table: "SoftSignalFeedbacks",
                columns: new[] { "UserId", "NewsDedupKey" },
                unique: true,
                filter: "\"Kind\" IN (3, 4)");

            migrationBuilder.CreateIndex(
                name: "UX_SoftSignalFeedback_Importance",
                table: "SoftSignalFeedbacks",
                columns: new[] { "UserId", "NewsDedupKey" },
                unique: true,
                filter: "\"Kind\" IN (1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_SoftSignalFeedback_Direction",
                table: "SoftSignalFeedbacks");

            migrationBuilder.DropIndex(
                name: "UX_SoftSignalFeedback_Importance",
                table: "SoftSignalFeedbacks");

            migrationBuilder.CreateIndex(
                name: "IX_SoftSignalFeedbacks_UserId_NewsDedupKey",
                table: "SoftSignalFeedbacks",
                columns: new[] { "UserId", "NewsDedupKey" },
                unique: true);
        }
    }
}
