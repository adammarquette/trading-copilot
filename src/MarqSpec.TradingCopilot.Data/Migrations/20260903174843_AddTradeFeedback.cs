using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTradeFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TradeFeedbacks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TradeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    EmotionalState = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Author = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradeFeedbacks", x => x.Id);
                    table.CheckConstraint("CK_TradeFeedback_Author_NotUnknown", "\"Author\" <> 0");
                    table.CheckConstraint("CK_TradeFeedback_HasContent", "\"Comment\" IS NOT NULL OR \"EmotionalState\" IS NOT NULL OR cardinality(\"Tags\") > 0");
                    table.ForeignKey(
                        name: "FK_TradeFeedbacks_Trades_TradeId",
                        column: x => x.TradeId,
                        principalTable: "Trades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TradeFeedbacks_TradeId_CreatedAt",
                table: "TradeFeedbacks",
                columns: new[] { "TradeId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TradeFeedbacks");
        }
    }
}
