using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SettleUpStartsWhenTracked : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Settle-up starts today for households that already named a partner (DECISIONS.md 92), and the app's
            // unconfirmed "paying back" guesses from before that go back to being her contribution, as they were.
            migrationBuilder.Sql("""
                UPDATE "Households" SET "PartnerSettledThrough" = CURRENT_DATE
                WHERE "PartnerName" IS NOT NULL AND "PartnerSettledThrough" IS NULL;
                """);
            migrationBuilder.Sql("""
                UPDATE "Transactions" t SET "Kind" = 7, "NeedsReview" = FALSE, "CategoryUnsure" = FALSE, "CategoryReason" = NULL
                FROM "Households" h
                WHERE h."Id" = t."HouseholdId" AND t."Kind" = 8 AND t."CategorySource" = 0 AND t."AmountCents" < 0
                  AND h."PartnerSettledThrough" IS NOT NULL AND t."Date" <= h."PartnerSettledThrough";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
