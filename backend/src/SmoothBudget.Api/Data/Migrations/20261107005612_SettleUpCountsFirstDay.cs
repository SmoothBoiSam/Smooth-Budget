using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SettleUpCountsFirstDay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Settle-up now counts its first day (DECISIONS.md 98). SettleUpStartsWhenTracked set the start to the day it
            // ran (2026-11-06) as "settled through"; move that one back a day so things dated that day count. A date the
            // user set with "Mark settled" isn't touched (that day was settled, so it stays excluded).
            migrationBuilder.Sql("""
                UPDATE "Households" SET "PartnerSettledThrough" = "PartnerSettledThrough" - 1
                WHERE "PartnerSettledThrough" = DATE '2026-11-06';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
