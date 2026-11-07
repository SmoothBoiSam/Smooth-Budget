using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class NoPartnerTally : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No more tally (DECISIONS.md 100): purchases entered as "paid by partner" and their "Paid by …" accounts go,
            // split purchases count in full again, and labels left by the old settle-up are cleared so a re-run asks again.
            migrationBuilder.Sql("""DELETE FROM "Transactions" WHERE "PaidByPartner";""");
            migrationBuilder.Sql("""
                DELETE FROM "Accounts" a WHERE a."Source" = 0 AND a."Name" LIKE 'Paid by %'
                  AND NOT EXISTS (SELECT 1 FROM "Transactions" t WHERE t."AccountId" = a."Id");
                """);
            migrationBuilder.Sql("""UPDATE "Transactions" SET "SplitFactor" = 1 WHERE "SplitFactor" < 1;""");
            migrationBuilder.Sql("""
                UPDATE "Transactions" SET "CategorySource" = 0, "NeedsReview" = TRUE
                WHERE "Kind" = 8 AND "CategorySource" = 4;
                """);

            migrationBuilder.DropColumn(
                name: "PaidByPartner",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "PartnerSharePercent",
                table: "Categories");

            migrationBuilder.RenameColumn(
                name: "PartnerSettledThrough",
                table: "Households",
                newName: "PartnerLabelsStartAfter");

            migrationBuilder.AddColumn<Guid>(
                name: "SuggestedCategoryId",
                table: "Transactions",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SuggestedCategoryId",
                table: "Transactions");

            migrationBuilder.RenameColumn(
                name: "PartnerLabelsStartAfter",
                table: "Households",
                newName: "PartnerSettledThrough");

            migrationBuilder.AddColumn<bool>(
                name: "PaidByPartner",
                table: "Transactions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PartnerSharePercent",
                table: "Categories",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
