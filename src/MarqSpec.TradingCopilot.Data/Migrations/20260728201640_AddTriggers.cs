using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTriggers : Migration
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
                    ArmCycle = table.Column<int>(type: "integer", nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Indicator = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Period = table.Column<int>(type: "integer", nullable: false),
                    ResolutionMinutes = table.Column<int>(type: "integer", nullable: false),
                    Comparison = table.Column<int>(type: "integer", nullable: false),
                    Threshold = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Value = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    FiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriggerFirings", x => x.Id);
                    table.CheckConstraint("CK_TriggerFirings_Comparison_NotUnknown", "\"Comparison\" <> 0");
                });

            migrationBuilder.CreateTable(
                name: "Triggers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Indicator = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Period = table.Column<int>(type: "integer", nullable: false),
                    ResolutionMinutes = table.Column<int>(type: "integer", nullable: false),
                    Comparison = table.Column<int>(type: "integer", nullable: false),
                    Threshold = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Route = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ArmState = table.Column<int>(type: "integer", nullable: false),
                    ArmCycle = table.Column<int>(type: "integer", nullable: false),
                    LastFiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SourceRuleId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Triggers", x => x.Id);
                    table.CheckConstraint("CK_Triggers_Comparison_NotUnknown", "\"Comparison\" <> 0");
                    table.CheckConstraint("CK_Triggers_Period_Positive", "\"Period\" > 0");
                    table.CheckConstraint("CK_Triggers_Resolution_Positive", "\"ResolutionMinutes\" > 0");
                    table.CheckConstraint("CK_Triggers_Route_NotUnknown", "\"Route\" <> 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_TriggerFirings_TriggerId_ArmCycle",
                table: "TriggerFirings",
                columns: new[] { "TriggerId", "ArmCycle" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TriggerFirings_UserId_FiredAt",
                table: "TriggerFirings",
                columns: new[] { "UserId", "FiredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Triggers_UserId_Enabled",
                table: "Triggers",
                columns: new[] { "UserId", "Enabled" });
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
