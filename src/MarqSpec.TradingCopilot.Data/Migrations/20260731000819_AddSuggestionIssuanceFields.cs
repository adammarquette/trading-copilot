using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSuggestionIssuanceFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.AddColumn<int>(
                name: "Confidence",
                table: "Suggestions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                table: "Suggestions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "Rationale",
                table: "Suggestions",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "TriggerFiringId",
                table: "Suggestions",
                type: "uuid",
                nullable: true);

            // BACKFILL BEFORE THE CHECK. The generated default for ExpiresAt is DateTimeOffset.MinValue, so on any
            // database that already holds suggestions every existing row would violate ExpiresAt > CreatedAt and the
            // constraint below would fail to apply -- the migration would abort halfway.
            //
            // Existing rows are backfilled to CreatedAt + 1 second: the fail-safe direction (their CreatedAt is in the
            // past, so they are ALREADY EXPIRED the moment the lifecycle sweep lands, gh#545) while still satisfying
            // the STRICT inequality. Backfilling to CreatedAt exactly -- the obvious reading -- would violate it.
            migrationBuilder.Sql(
                @"UPDATE ""Suggestions"" SET ""ExpiresAt"" = ""CreatedAt"" + INTERVAL '1 second';");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Suggestions_Confidence_Range",
                table: "Suggestions",
                sql: "\"Confidence\" BETWEEN 0 AND 100");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Suggestions_ExpiresAfterCreated",
                table: "Suggestions",
                sql: "\"ExpiresAt\" > \"CreatedAt\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Suggestions_Confidence_Range",
                table: "Suggestions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Suggestions_ExpiresAfterCreated",
                table: "Suggestions");

            migrationBuilder.DropColumn(
                name: "CitedIndicator",
                table: "Suggestions");

            migrationBuilder.DropColumn(
                name: "CitedPeriod",
                table: "Suggestions");

            migrationBuilder.DropColumn(
                name: "CitedResolutionMinutes",
                table: "Suggestions");

            migrationBuilder.DropColumn(
                name: "Confidence",
                table: "Suggestions");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "Suggestions");

            migrationBuilder.DropColumn(
                name: "Rationale",
                table: "Suggestions");

            migrationBuilder.DropColumn(
                name: "TriggerFiringId",
                table: "Suggestions");
        }
    }
}
