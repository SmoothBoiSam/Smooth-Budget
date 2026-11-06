using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReviewEveryCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CategoryUnsure",
                table: "Transactions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Until now NeedsReview meant "unsure"; it now means "automatic and not yet confirmed" (DECISIONS.md 80).
            // Carry the old flag over as "unsure", then queue every automatic category (bank 1, rule 2, learned 5).
            migrationBuilder.Sql("""UPDATE "Transactions" SET "CategoryUnsure" = "NeedsReview";""");
            migrationBuilder.Sql("""
                UPDATE "Transactions" SET "NeedsReview" = TRUE
                WHERE "CategoryId" IS NOT NULL AND NOT "IsTransfer" AND "CategorySource" IN (1, 2, 5);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CategoryUnsure",
                table: "Transactions");
        }
    }
}
