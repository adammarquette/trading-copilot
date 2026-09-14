using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventBackbone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventCursors",
                columns: table => new
                {
                    ConsumerGroup = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LastSequence = table.Column<long>(type: "bigint", nullable: false),
                    CommittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventCursors", x => x.ConsumerGroup);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Source = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    TraceParent = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Sequence);
                });

            // The log is append-only and EF never updates it, so it needs no DB-enforced key -- and a
            // hypertable's unique constraints would have to include the time dimension anyway. Sequence stays
            // the logical key; the identity generator supplies uniqueness; consumers scan by it via the index.
            migrationBuilder.Sql("""
                ALTER TABLE "Events" DROP CONSTRAINT "PK_Events";
                CREATE INDEX "IX_Events_Sequence" ON "Events" ("Sequence");
                """);

            // Hypertable + retention (ADR-0001) where TimescaleDB is available; a plain table elsewhere -- the
            // backbone still works, it just misses compression/retention/continuous-aggregates. Raw SQL on
            // purpose: EF has no vocabulary for any of this. Retention: 24h, the ADR's outer bound (the log is
            // a short-lived real-time pipeline; the poller's clean historical store is the long-lived record).
            migrationBuilder.Sql("""
                DO $body$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb') THEN
                        CREATE EXTENSION IF NOT EXISTS timescaledb;
                        PERFORM create_hypertable('"Events"', by_range('RecordedAt'));
                        PERFORM add_retention_policy('"Events"', INTERVAL '24 hours');
                    ELSE
                        RAISE WARNING 'timescaledb unavailable: "Events" stays a plain table (no hypertable, no retention policy) — ADR-0001 degrades gracefully';
                    END IF;
                END
                $body$;
                """);

            // LISTEN/NOTIFY wake-up (ADR-0001): every append notifies; the LISTEN side arrives with the first
            // live consumer. Plain Postgres -- works with or without Timescale.
            migrationBuilder.Sql("""
                CREATE FUNCTION notify_event_appended() RETURNS trigger AS $$
                BEGIN
                    PERFORM pg_notify('events_appended', NEW."Sequence"::text);
                    RETURN NEW;
                END $$ LANGUAGE plpgsql;

                CREATE TRIGGER tr_events_notify
                    AFTER INSERT ON "Events"
                    FOR EACH ROW EXECUTE FUNCTION notify_event_appended();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The trigger falls with the table; the function must be dropped explicitly.
            migrationBuilder.Sql("""DROP FUNCTION IF EXISTS notify_event_appended() CASCADE;""");

            migrationBuilder.DropTable(
                name: "EventCursors");

            migrationBuilder.DropTable(
                name: "Events");
        }
    }
}
