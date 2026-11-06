using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AutoCategorizeAndPartner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BankCategoryPrimary",
                table: "Transactions",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BankConfidence",
                table: "Transactions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CategoryReason",
                table: "Transactions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NeedsReview",
                table: "Transactions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PartnerName",
                table: "Households",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PartnerExpectedCents",
                table: "BudgetMonths",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BankCategoryPrimary",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "BankConfidence",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "CategoryReason",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "NeedsReview",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "PartnerName",
                table: "Households");

            migrationBuilder.DropColumn(
                name: "PartnerExpectedCents",
                table: "BudgetMonths");
        }
    }
}
