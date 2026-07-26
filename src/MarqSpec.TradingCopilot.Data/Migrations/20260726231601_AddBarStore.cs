using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBarStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Bars",
                columns: table => new
                {
                    Venue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResolutionMinutes = table.Column<int>(type: "integer", nullable: false),
                    BucketStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Open = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    High = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Low = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Close = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Volume = table.Column<long>(type: "bigint", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bars", x => new { x.Venue, x.Instrument, x.ResolutionMinutes, x.BucketStart });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Bars_Instrument_ResolutionMinutes_BucketStart",
                table: "Bars",
                columns: new[] { "Instrument", "ResolutionMinutes", "BucketStart" });

            // Hypertable where TimescaleDB is available; a plain table elsewhere -- the store still works, it
            // just misses partitioning and compression. The AddEventBackbone precedent, and the same reason for
            // raw SQL: EF has no vocabulary for any of it.
            //
            // The primary key already contains BucketStart, which is what makes this legal: a hypertable's
            // unique constraints must include the partitioning column. That key is also the idempotence guard --
            // an overlapping re-poll can only UPDATE the bucket it already wrote (R-1's system-of-record
            // property depends on it, so it is enforced here rather than trusted to the writer).
            //
            // DELIBERATELY NO RETENTION POLICY, unlike "Events" (24h). This table is the CLEAN-HISTORICAL
            // SYSTEM OF RECORD (R-1): the authoritative series journaling and replay read (R-9), and the store
            // ADR-0001 means when it says a full indicator rebuild "reprocesses the clean historical store, not
            // the short event log". Expiring it on a timer would defeat the thing it exists to be. Growth is
            // bounded by watchlist x resolutions rather than by time; an operator who wants a ceiling adds a
            // policy deliberately, and compression is the better first lever (a follow-up, not this increment).
            migrationBuilder.Sql("""
                DO $body$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb') THEN
                        CREATE EXTENSION IF NOT EXISTS timescaledb;
                        PERFORM create_hypertable('"Bars"', by_range('BucketStart'), migrate_data => true);
                    ELSE
                        RAISE WARNING 'timescaledb unavailable: "Bars" stays a plain table (no hypertable) — the clean-historical store degrades gracefully, as ADR-0001 does';
                    END IF;
                END
                $body$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Bars");
        }
    }
}
