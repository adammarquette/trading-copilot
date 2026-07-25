using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<int>(type: "integer", nullable: false),
                    Placement = table.Column<int>(type: "integer", nullable: false),
                    SyntheticRisk = table.Column<bool>(type: "boolean", nullable: false),
                    StopPlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    Before = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    After = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Detail = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditRecords", x => x.Id);
                    table.CheckConstraint("CK_AuditRecords_Action_NotUnknown", "\"Action\" <> 0");
                    table.CheckConstraint("CK_AuditRecords_Placement_NotUnknown", "\"Placement\" <> 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditRecords_UserId_RecordedAt",
                table: "AuditRecords",
                columns: new[] { "UserId", "RecordedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditRecords");
        }
    }
}
