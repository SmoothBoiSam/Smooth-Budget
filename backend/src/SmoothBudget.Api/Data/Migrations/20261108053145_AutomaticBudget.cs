using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AutomaticBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoBudget",
                table: "Households",
                type: "boolean",
                nullable: false,
                defaultValue: true); // existing households get the automatic budget; new ones follow Budget:AutoForNewHouseholds

            migrationBuilder.AddColumn<bool>(
                name: "LimitSetByUser",
                table: "Categories",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AutoNote",
                table: "BudgetMonths",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AutoNoteUtc",
                table: "BudgetMonths",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AutoShortCents",
                table: "BudgetMonths",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "IncomeSetByUser",
                table: "BudgetMonths",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoBudget",
                table: "Households");

            migrationBuilder.DropColumn(
                name: "LimitSetByUser",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "AutoNote",
                table: "BudgetMonths");

            migrationBuilder.DropColumn(
                name: "AutoNoteUtc",
                table: "BudgetMonths");

            migrationBuilder.DropColumn(
                name: "AutoShortCents",
                table: "BudgetMonths");

            migrationBuilder.DropColumn(
                name: "IncomeSetByUser",
                table: "BudgetMonths");
        }
    }
}
