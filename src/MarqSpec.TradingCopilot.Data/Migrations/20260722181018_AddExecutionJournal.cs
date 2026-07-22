using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Mode",
                table: "Accounts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Suggestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Instrument = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Side = table.Column<int>(type: "integer", nullable: false),
                    Size = table.Column<int>(type: "integer", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    StopPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    TargetPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suggestions", x => x.Id);
                    table.CheckConstraint("CK_Suggestions_Mode_NotUndeclared", "\"Mode\" <> 0");
                    table.CheckConstraint("CK_Suggestions_Size_Positive", "\"Size\" > 0");
                    table.CheckConstraint("CK_Suggestions_State_NotUnknown", "\"State\" <> 0");
                    table.ForeignKey(
                        name: "FK_Suggestions_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    SuggestionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Instrument = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Side = table.Column<int>(type: "integer", nullable: false),
                    Size = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    LimitPrice = table.Column<decimal>(type: "numeric", nullable: true),
                    StopPrice = table.Column<decimal>(type: "numeric", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    VenueOrderKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PlacedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                    table.CheckConstraint("CK_Orders_Mode_NotUndeclared", "\"Mode\" <> 0");
                    table.CheckConstraint("CK_Orders_Size_Positive", "\"Size\" > 0");
                    table.CheckConstraint("CK_Orders_Status_NotUnknown", "\"Status\" <> 0");
                    table.ForeignKey(
                        name: "FK_Orders_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Orders_Suggestions_SuggestionId",
                        column: x => x.SuggestionId,
                        principalTable: "Suggestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Trades",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    SuggestionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Instrument = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Side = table.Column<int>(type: "integer", nullable: false),
                    Size = table.Column<int>(type: "integer", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    ExitPrice = table.Column<decimal>(type: "numeric", nullable: true),
                    RealizedPnL = table.Column<decimal>(type: "numeric", nullable: true),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trades", x => x.Id);
                    table.CheckConstraint("CK_Trades_Mode_NotUndeclared", "\"Mode\" <> 0");
                    table.CheckConstraint("CK_Trades_Size_Positive", "\"Size\" > 0");
                    table.ForeignKey(
                        name: "FK_Trades_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Trades_Suggestions_SuggestionId",
                        column: x => x.SuggestionId,
                        principalTable: "Suggestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Fills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    VenueFillKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Price = table.Column<decimal>(type: "numeric", nullable: false),
                    Size = table.Column<int>(type: "integer", nullable: false),
                    Fees = table.Column<decimal>(type: "numeric", nullable: false),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Fills", x => x.Id);
                    table.CheckConstraint("CK_Fills_Size_Positive", "\"Size\" > 0");
                    table.ForeignKey(
                        name: "FK_Fills_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Fills_OrderId_VenueFillKey",
                table: "Fills",
                columns: new[] { "OrderId", "VenueFillKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_AccountId_VenueOrderKey",
                table: "Orders",
                columns: new[] { "AccountId", "VenueOrderKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_SuggestionId",
                table: "Orders",
                column: "SuggestionId");

            migrationBuilder.CreateIndex(
                name: "IX_Suggestions_AccountId",
                table: "Suggestions",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Trades_AccountId",
                table: "Trades",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Trades_SuggestionId",
                table: "Trades",
                column: "SuggestionId");

            // Backfill the persisted Account.Mode (raw SQL on purpose: EF has no vocabulary for it) from the
            // same rule every write point applies -- effective stage (StageOverride ?? Stage) x the firm's
            // declared conventions. No matching declaration => 0 (Undeclared), the fail-closed zero.
            migrationBuilder.Sql("""
                UPDATE "Accounts" AS account
                SET "Mode" = COALESCE(
                    (SELECT CASE WHEN convention."CapitalAtRisk" THEN 2 ELSE 1 END
                     FROM "Connections" AS connection
                     JOIN "FirmStageConventions" AS convention
                       ON convention."FirmId" = connection."FirmId"
                      AND convention."Stage" = COALESCE(account."StageOverride", account."Stage")
                     WHERE connection."Id" = account."ConnectionId"),
                    0);
                """);

            // The cross-table half of the R-14 persistence guard (gh#7, carried from #78): Order.mode and
            // Suggestion.mode must equal their account's persisted mode AT WRITE TIME. A single-row CHECK
            // cannot express a cross-table rule, so this is a constraint trigger -- raw SQL on purpose. It
            // fires on the child tables only: an account's mode may legitimately move later (override,
            // re-declaration), and history keeps its placement-time mode.
            migrationBuilder.Sql("""
                CREATE FUNCTION enforce_mode_matches_account() RETURNS trigger AS $$
                BEGIN
                    IF NEW."Mode" IS DISTINCT FROM
                        (SELECT "Mode" FROM "Accounts" WHERE "Id" = NEW."AccountId") THEN
                        RAISE EXCEPTION 'R-14 mode guard: % row mode % does not match the account''s current mode',
                            TG_TABLE_NAME, NEW."Mode";
                    END IF;
                    RETURN NEW;
                END $$ LANGUAGE plpgsql;

                CREATE CONSTRAINT TRIGGER ct_orders_mode_matches_account
                    AFTER INSERT OR UPDATE OF "Mode", "AccountId" ON "Orders"
                    FOR EACH ROW EXECUTE FUNCTION enforce_mode_matches_account();

                CREATE CONSTRAINT TRIGGER ct_suggestions_mode_matches_account
                    AFTER INSERT OR UPDATE OF "Mode", "AccountId" ON "Suggestions"
                    FOR EACH ROW EXECUTE FUNCTION enforce_mode_matches_account();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Triggers fall with their tables; the shared function must be dropped explicitly.
            migrationBuilder.Sql("""DROP FUNCTION IF EXISTS enforce_mode_matches_account() CASCADE;""");

            migrationBuilder.DropTable(
                name: "Fills");

            migrationBuilder.DropTable(
                name: "Trades");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "Suggestions");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "Accounts");
        }
    }
}
