using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSuggestionSupersede : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SupersedesId",
                table: "Suggestions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Suggestions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_Suggestions_SupersedesId",
                table: "Suggestions",
                column: "SupersedesId");

            migrationBuilder.AddForeignKey(
                name: "FK_Suggestions_Suggestions_SupersedesId",
                table: "Suggestions",
                column: "SupersedesId",
                principalTable: "Suggestions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Suggestions_Suggestions_SupersedesId",
                table: "Suggestions");

            migrationBuilder.DropIndex(
                name: "IX_Suggestions_SupersedesId",
                table: "Suggestions");

            migrationBuilder.DropColumn(
                name: "SupersedesId",
                table: "Suggestions");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Suggestions");
        }
    }
}
