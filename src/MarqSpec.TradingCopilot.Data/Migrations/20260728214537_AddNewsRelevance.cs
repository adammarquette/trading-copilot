using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNewsRelevance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Explicit default so the ALTER can backfill the already-populated News table (gh#358 ingestion is
            // always on) -- the entity's `= []` initializer produces no DB default, and NOT NULL without one aborts
            // on existing rows.
            migrationBuilder.AddColumn<List<string>>(
                name: "MatchedInstruments",
                table: "News",
                type: "text[]",
                nullable: false,
                defaultValue: new List<string>());

            migrationBuilder.AddColumn<List<string>>(
                name: "MatchedTopics",
                table: "News",
                type: "text[]",
                nullable: false,
                defaultValue: new List<string>());

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RelevanceResolvedAt",
                table: "News",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NewsTopics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Keywords = table.Column<List<string>>(type: "text[]", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsTopics", x => x.Id);
                    table.CheckConstraint("CK_NewsTopics_Scope_NotUnknown", "\"Scope\" <> 0");
                });

            migrationBuilder.CreateTable(
                name: "RelevanceConfigStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelevanceConfigStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TickerInstrumentMaps",
                columns: table => new
                {
                    Ticker = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TickerInstrumentMaps", x => new { x.Ticker, x.Instrument });
                });

            migrationBuilder.CreateIndex(
                name: "IX_News_RelevanceResolvedAt",
                table: "News",
                column: "RelevanceResolvedAt");

            migrationBuilder.CreateIndex(
                name: "IX_NewsTopics_Name",
                table: "NewsTopics",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NewsTopics");

            migrationBuilder.DropTable(
                name: "RelevanceConfigStates");

            migrationBuilder.DropTable(
                name: "TickerInstrumentMaps");

            migrationBuilder.DropIndex(
                name: "IX_News_RelevanceResolvedAt",
                table: "News");

            migrationBuilder.DropColumn(
                name: "MatchedInstruments",
                table: "News");

            migrationBuilder.DropColumn(
                name: "MatchedTopics",
                table: "News");

            migrationBuilder.DropColumn(
                name: "RelevanceResolvedAt",
                table: "News");
        }
    }
}
