using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <summary>
    /// Bill categories added from Review came in at $0 and were treated as a limit you set, so the automatic budget never
    /// filled them in (DECISIONS.md 158). Hand those back to it. Type 0 is a fixed bill.
    /// </summary>
    public partial class AutomaticNewBills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Categories" SET "LimitSetByUser" = FALSE
                WHERE "LimitSetByUser" AND "MonthlyLimitCents" = 0 AND "Type" = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: which limits were yours before isn't kept.
        }
    }
}
