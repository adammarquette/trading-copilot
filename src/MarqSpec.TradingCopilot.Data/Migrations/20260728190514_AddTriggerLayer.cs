using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTriggerLayer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TriggerFirings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggerId = table.Column<Guid>(type: "uuid", nullable: false),
                    FiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ObservedValue = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Threshold = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Comparison = table.Column<int>(type: "integer", nullable: false),
                    DedupKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriggerFirings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Triggers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Indicator = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Period = table.Column<int>(type: "integer", nullable: false),
                    ResolutionMinutes = table.Column<int>(type: "integer", nullable: false),
                    ConditionKind = table.Column<int>(type: "integer", nullable: false),
                    Comparison = table.Column<int>(type: "integer", nullable: false),
                    Threshold = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Hysteresis = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    Route = table.Column<int>(type: "integer", nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ArmState = table.Column<int>(type: "integer", nullable: false),
                    ArmCycle = table.Column<int>(type: "integer", nullable: false),
                    LastEvaluatedValue = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    LastFiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SourceRuleId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Triggers", x => x.Id);
                    table.CheckConstraint("CK_Triggers_Comparison_NotUnknown", "\"Comparison\" <> 0");
                    table.CheckConstraint("CK_Triggers_ConditionKind_NotUnknown", "\"ConditionKind\" <> 0");
                    table.CheckConstraint("CK_Triggers_Hysteresis_PositiveOrNull", "\"Hysteresis\" IS NULL OR \"Hysteresis\" > 0");
                    table.CheckConstraint("CK_Triggers_Period_Positive", "\"Period\" > 0");
                    table.CheckConstraint("CK_Triggers_ResolutionMinutes_Positive", "\"ResolutionMinutes\" > 0");
                    table.CheckConstraint("CK_Triggers_Route_NotUnknown", "\"Route\" <> 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_TriggerFirings_TriggerId",
                table: "TriggerFirings",
                column: "TriggerId");

            migrationBuilder.CreateIndex(
                name: "IX_Triggers_Enabled_Route",
                table: "Triggers",
                columns: new[] { "Enabled", "Route" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TriggerFirings");

            migrationBuilder.DropTable(
                name: "Triggers");
        }
    }
}
