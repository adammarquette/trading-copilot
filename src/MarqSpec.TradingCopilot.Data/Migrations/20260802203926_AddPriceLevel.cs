using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PriceLevels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Venue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TimeframeMinutes = table.Column<int>(type: "integer", nullable: false),
                    Top = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Bottom = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Significance = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    FormedAtBucket = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TouchCount = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceLevels", x => x.Id);
                    table.CheckConstraint("CK_PriceLevels_Bottom_Positive", "\"Bottom\" > 0");
                    table.CheckConstraint("CK_PriceLevels_Kind_NotUnknown", "\"Kind\" <> 0");
                    table.CheckConstraint("CK_PriceLevels_Timeframe_Positive", "\"TimeframeMinutes\" > 0");
                    table.CheckConstraint("CK_PriceLevels_ZoneOrdered", "\"Top\" > \"Bottom\"");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PriceLevels_Venue_Instrument_TimeframeMinutes_Active",
                table: "PriceLevels",
                columns: new[] { "Venue", "Instrument", "TimeframeMinutes", "Active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PriceLevels");
        }
    }
}
