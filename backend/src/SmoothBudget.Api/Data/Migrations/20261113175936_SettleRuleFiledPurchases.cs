using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SettleRuleFiledPurchases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data only (DECISIONS.md 252). A rule made with "always" settles the purchases already there (224), but every
            // later re-filing run put them back in Review. Settle them again: purchases still filed by a rule, in that
            // rule's category, that were already there when the rule was made. Nothing is re-filed; only the flag changes.
            migrationBuilder.Sql("""
                UPDATE "Transactions" t SET "NeedsReview" = FALSE, "CategoryUnsure" = FALSE
                FROM "CategoryRules" r
                WHERE r."HouseholdId" = t."HouseholdId" AND r."CategoryId" = t."CategoryId"
                  AND t."CategorySource" = 2 AND t."NeedsReview" AND NOT t."IsTransfer"
                  AND t."CategoryReason" = 'Your rule for "' || r."Pattern" || '".'
                  AND t."CreatedUtc" < r."CreatedUtc";
                """);
            // A transfer isn't waiting for review: clear what was left from the guess it had before it was recognized.
            migrationBuilder.Sql("""
                UPDATE "Transactions" SET "NeedsReview" = FALSE, "CategoryUnsure" = FALSE, "SuggestedCategoryId" = NULL, "CategoryReason" = NULL
                WHERE "IsTransfer" AND "NeedsReview";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: which rows were flagged isn't kept.
        }
    }
}
