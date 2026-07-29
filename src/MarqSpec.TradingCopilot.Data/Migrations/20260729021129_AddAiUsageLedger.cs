using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiUsageLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiUsage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Feature = table.Column<int>(type: "integer", nullable: false),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Tier = table.Column<int>(type: "integer", nullable: true),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    InputTokens = table.Column<int>(type: "integer", nullable: false),
                    OutputTokens = table.Column<int>(type: "integer", nullable: false),
                    EstimatedCostUsd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    LatencyMs = table.Column<long>(type: "bigint", nullable: false),
                    TraceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsage", x => x.Id);
                    table.CheckConstraint("CK_AiUsage_EstimatedCostUsd_NotNegative", "\"EstimatedCostUsd\" >= 0");
                    table.CheckConstraint("CK_AiUsage_Feature_NotUnknown", "\"Feature\" <> 0");
                    table.CheckConstraint("CK_AiUsage_InputTokens_NotNegative", "\"InputTokens\" >= 0");
                    table.CheckConstraint("CK_AiUsage_LatencyMs_NotNegative", "\"LatencyMs\" >= 0");
                    table.CheckConstraint("CK_AiUsage_Outcome_NotUnknown", "\"Outcome\" <> 0");
                    table.CheckConstraint("CK_AiUsage_OutputTokens_NotNegative", "\"OutputTokens\" >= 0");
                    table.CheckConstraint("CK_AiUsage_Tier_NotUnknownOrNull", "\"Tier\" IS NULL OR \"Tier\" <> 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsage_UserId_OccurredAt",
                table: "AiUsage",
                columns: new[] { "UserId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiUsage");
        }
    }
}
