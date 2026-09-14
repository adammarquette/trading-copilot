using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSuggestionCitedFactors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // (1) The cited-factor set (gh#729, ADR-0026): one child table, one primary + supporting per suggestion.
            migrationBuilder.CreateTable(
                name: "SuggestionCitedFactors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SuggestionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    TimeframeMinutes = table.Column<int>(type: "integer", nullable: false),
                    Indicator = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Period = table.Column<int>(type: "integer", nullable: true),
                    LevelId = table.Column<Guid>(type: "uuid", nullable: true),
                    LevelVenue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LevelKind = table.Column<int>(type: "integer", nullable: true),
                    LevelTop = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    LevelBottom = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    LevelSignificance = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SuggestionCitedFactors", x => x.Id);
                    table.CheckConstraint("CK_SuggestionCitedFactors_Kind_NotUnknown", "\"Kind\" <> 0");
                    table.CheckConstraint("CK_SuggestionCitedFactors_KindColumns", "(\"Kind\" = 1 AND \"Indicator\" IS NOT NULL AND \"Period\" IS NOT NULL AND \"LevelId\" IS NULL AND \"LevelVenue\" IS NULL AND \"LevelKind\" IS NULL AND \"LevelTop\" IS NULL AND \"LevelBottom\" IS NULL AND \"LevelSignificance\" IS NULL) OR (\"Kind\" = 2 AND \"Indicator\" IS NULL AND \"Period\" IS NULL AND \"LevelId\" IS NOT NULL AND \"LevelVenue\" IS NOT NULL AND \"LevelKind\" IS NOT NULL AND \"LevelTop\" IS NOT NULL AND \"LevelBottom\" IS NOT NULL AND \"LevelSignificance\" IS NOT NULL)");
                    table.CheckConstraint("CK_SuggestionCitedFactors_LevelKind_NotUnknown", "\"LevelKind\" IS NULL OR \"LevelKind\" <> 0");
                    table.CheckConstraint("CK_SuggestionCitedFactors_LevelZoneOrdered", "\"LevelTop\" IS NULL OR \"LevelBottom\" IS NULL OR \"LevelTop\" > \"LevelBottom\"");
                    table.CheckConstraint("CK_SuggestionCitedFactors_Timeframe_Positive", "\"TimeframeMinutes\" > 0");
                    table.ForeignKey(
                        name: "FK_SuggestionCitedFactors_Suggestions_SuggestionId",
                        column: x => x.SuggestionId,
                        principalTable: "Suggestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SuggestionCitedFactors_SuggestionId",
                table: "SuggestionCitedFactors",
                column: "SuggestionId");

            migrationBuilder.CreateIndex(
                name: "UX_SuggestionCitedFactors_OnePrimary",
                table: "SuggestionCitedFactors",
                column: "SuggestionId",
                unique: true,
                filter: "\"IsPrimary\"");

            // (2) BACKFILL BEFORE THE DROP — the set becomes the single source of truth (gh#729). Every existing
            // suggestion becomes exactly ONE primary (Kind = 1 = Indicator) factor carrying its old cited signal.
            // gen_random_uuid() is built into PostgreSQL 13+ (the deployment runs pg17). TimeframeMinutes uses
            // GREATEST(..., 1) so a pre-gh#542 row whose CitedResolutionMinutes defaulted to 0 still satisfies
            // CK_SuggestionCitedFactors_Timeframe_Positive rather than aborting the migration — the fail-safe
            // backfill-before-constraint pattern AddSuggestionIssuanceFields used for ExpiresAt. Indicator/Period were
            // NOT NULL columns, so the Kind = 1 arm of CK_SuggestionCitedFactors_KindColumns holds.
            migrationBuilder.Sql(
                @"INSERT INTO ""SuggestionCitedFactors""
                      (""Id"", ""UserId"", ""SuggestionId"", ""Kind"", ""IsPrimary"", ""TimeframeMinutes"", ""Indicator"", ""Period"")
                  SELECT gen_random_uuid(), ""UserId"", ""Id"", 1, true, GREATEST(""CitedResolutionMinutes"", 1), ""CitedIndicator"", ""CitedPeriod""
                  FROM ""Suggestions"";");

            // (3) Drop the old single-signal columns — after the backfill, nothing reads them (the read model
            // reconstructs the headline from the primary factor).
            migrationBuilder.DropColumn(
                name: "CitedIndicator",
                table: "Suggestions");

            migrationBuilder.DropColumn(
                name: "CitedPeriod",
                table: "Suggestions");

            migrationBuilder.DropColumn(
                name: "CitedResolutionMinutes",
                table: "Suggestions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // (1) Re-add the old single-signal columns (non-null with the original defaults, matching the schema
            // AddSuggestionIssuanceFields created).
            migrationBuilder.AddColumn<string>(
                name: "CitedIndicator",
                table: "Suggestions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "CitedPeriod",
                table: "Suggestions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CitedResolutionMinutes",
                table: "Suggestions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // (2) Backfill them from the PRIMARY indicator factor BEFORE dropping the table — the reverse of Up's
            // single-source move. Only an indicator primary (Kind = 1) can be represented by the old columns; a
            // level-primary suggestion (none exist in the gh#729 N=1 model) keeps the defaults.
            migrationBuilder.Sql(
                @"UPDATE ""Suggestions"" AS s
                  SET ""CitedIndicator"" = COALESCE(f.""Indicator"", ''),
                      ""CitedPeriod"" = COALESCE(f.""Period"", 0),
                      ""CitedResolutionMinutes"" = f.""TimeframeMinutes""
                  FROM ""SuggestionCitedFactors"" AS f
                  WHERE f.""SuggestionId"" = s.""Id"" AND f.""IsPrimary"" = true AND f.""Kind"" = 1;");

            // (3) Drop the set.
            migrationBuilder.DropTable(
                name: "SuggestionCitedFactors");
        }
    }
}
