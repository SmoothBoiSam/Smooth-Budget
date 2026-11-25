using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemovePlannedExpenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Planned expenses were taken out again (DECISIONS.md 327). A database that ran their migration still has
            // the table and column: their bill lines become ordinary ones (hidden if nothing's filed there), then both go.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF to_regclass('"PlannedExpenses"') IS NOT NULL THEN
                        UPDATE "Categories" c
                        SET "MonthlyLimitCents" = 0,
                            "LimitSetByUser" = false,
                            "IsHidden" = c."IsHidden" OR NOT EXISTS (SELECT 1 FROM "Transactions" t WHERE t."CategoryId" = c."Id")
                        WHERE c."Id" IN (SELECT "CategoryId" FROM "PlannedExpenses");
                        DROP TABLE "PlannedExpenses";
                    END IF;
                END $$;
                ALTER TABLE "BudgetMonths" DROP COLUMN IF EXISTS "SetAsideInCents";
                DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125073145_PlannedExpenses';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
