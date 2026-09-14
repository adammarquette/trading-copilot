using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIndicatorValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IndicatorValues",
                columns: table => new
                {
                    Venue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResolutionMinutes = table.Column<int>(type: "integer", nullable: false),
                    Indicator = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Period = table.Column<int>(type: "integer", nullable: false),
                    BucketStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Value = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndicatorValues", x => new { x.Venue, x.Instrument, x.ResolutionMinutes, x.Indicator, x.Period, x.BucketStart });
                });

            migrationBuilder.CreateIndex(
                name: "IX_IndicatorValues_Instrument_ResolutionMinutes_Indicator_Peri~",
                table: "IndicatorValues",
                columns: new[] { "Instrument", "ResolutionMinutes", "Indicator", "Period", "BucketStart" });

            // Hypertable where TimescaleDB is available; a plain table elsewhere -- the AddEventBackbone and
            // AddBarStore precedent, and the same reason for raw SQL: EF has no vocabulary for it. The primary
            // key already contains BucketStart, which is what makes the conversion legal.
            //
            // NO RETENTION POLICY, like the bars beneath it. These values are a PROJECTION -- reproducible from
            // BarRecord at any time (ADR-0001: rebuild = replay) -- so they could in principle be expired and
            // recomputed. They are kept because the store they derive from is kept: a journal entry or a replay
            // that reaches for the ATR behind a historical decision should find the number that was actually
            // used, not today's recomputation of it.
            migrationBuilder.Sql("""
                DO $body$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb') THEN
                        CREATE EXTENSION IF NOT EXISTS timescaledb;
                        PERFORM create_hypertable('"IndicatorValues"', by_range('BucketStart'), migrate_data => true);
                    ELSE
                        RAISE WARNING 'timescaledb unavailable: "IndicatorValues" stays a plain table (no hypertable) — degrades gracefully, as ADR-0001 does';
                    END IF;
                END
                $body$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IndicatorValues");
        }
    }
}
