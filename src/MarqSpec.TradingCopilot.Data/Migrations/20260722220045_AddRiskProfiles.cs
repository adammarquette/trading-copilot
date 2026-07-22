using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRiskProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RiskProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    DailyLossLimit = table.Column<decimal>(type: "numeric", nullable: true),
                    AccountProfitTarget = table.Column<decimal>(type: "numeric", nullable: true),
                    FloorSource = table.Column<int>(type: "integer", nullable: false),
                    TrailingMode = table.Column<int>(type: "integer", nullable: false),
                    TrailingAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    LocksAt = table.Column<decimal>(type: "numeric", nullable: true),
                    PerTradeRiskFraction = table.Column<decimal>(type: "numeric", nullable: false),
                    TargetRewardRatio = table.Column<decimal>(type: "numeric", nullable: false),
                    MaxDrawdownPerTrade = table.Column<decimal>(type: "numeric", nullable: false),
                    DailyDrawdownGovernor = table.Column<decimal>(type: "numeric", nullable: false),
                    DailyProfitTarget = table.Column<decimal>(type: "numeric", nullable: true),
                    StopForDayAtProfitTarget = table.Column<bool>(type: "boolean", nullable: false),
                    SizingBasis = table.Column<int>(type: "integer", nullable: false),
                    MaxContractsPerOrder = table.Column<int>(type: "integer", nullable: false),
                    MaxBestDayFraction = table.Column<decimal>(type: "numeric", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskProfiles", x => x.Id);
                    table.CheckConstraint("CK_RiskProfiles_DailyDrawdownGovernor_Positive", "\"DailyDrawdownGovernor\" > 0");
                    table.CheckConstraint("CK_RiskProfiles_MaxBestDayFraction_ZeroToOne", "\"MaxBestDayFraction\" > 0 AND \"MaxBestDayFraction\" <= 1");
                    table.CheckConstraint("CK_RiskProfiles_MaxContractsPerOrder_NotNegative", "\"MaxContractsPerOrder\" >= 0");
                    table.CheckConstraint("CK_RiskProfiles_MaxDrawdownPerTrade_Positive", "\"MaxDrawdownPerTrade\" > 0");
                    table.CheckConstraint("CK_RiskProfiles_PerTradeRiskFraction_ZeroToOne", "\"PerTradeRiskFraction\" > 0 AND \"PerTradeRiskFraction\" <= 1");
                    table.CheckConstraint("CK_RiskProfiles_TargetRewardRatio_Positive", "\"TargetRewardRatio\" > 0");
                    table.CheckConstraint("CK_RiskProfiles_TrailingAmount_Positive", "\"TrailingAmount\" > 0");
                    table.ForeignKey(
                        name: "FK_RiskProfiles_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RiskProfiles_AccountId",
                table: "RiskProfiles",
                column: "AccountId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RiskProfiles");
        }
    }
}
