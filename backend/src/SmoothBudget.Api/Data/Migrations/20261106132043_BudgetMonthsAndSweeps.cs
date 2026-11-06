using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BudgetMonthsAndSweeps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SweepAmountCents",
                table: "BudgetMonths");

            migrationBuilder.DropColumn(
                name: "SweepDestination",
                table: "BudgetMonths");

            migrationBuilder.AddColumn<int>(
                name: "SweepSavingsPercent",
                table: "Households",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SweepTarget",
                table: "Households",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedUtc",
                table: "BudgetMonths",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BudgetMonthCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetMonthId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    LimitOverrideCents = table.Column<long>(type: "bigint", nullable: true),
                    ClosingLimitCents = table.Column<long>(type: "bigint", nullable: true),
                    ClosingSpentCents = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetMonthCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BudgetMonthCategories_BudgetMonths_BudgetMonthId",
                        column: x => x.BudgetMonthId,
                        principalTable: "BudgetMonths",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BudgetMonthCategories_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SweepTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetMonthId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    AmountCents = table.Column<long>(type: "bigint", nullable: false),
                    Destination = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DoneUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SweepTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SweepTasks_BudgetMonths_BudgetMonthId",
                        column: x => x.BudgetMonthId,
                        principalTable: "BudgetMonths",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetMonthCategories_BudgetMonthId_CategoryId",
                table: "BudgetMonthCategories",
                columns: new[] { "BudgetMonthId", "CategoryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BudgetMonthCategories_CategoryId",
                table: "BudgetMonthCategories",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_SweepTasks_BudgetMonthId",
                table: "SweepTasks",
                column: "BudgetMonthId");

            migrationBuilder.CreateIndex(
                name: "IX_SweepTasks_HouseholdId_DoneUtc",
                table: "SweepTasks",
                columns: new[] { "HouseholdId", "DoneUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BudgetMonthCategories");

            migrationBuilder.DropTable(
                name: "SweepTasks");

            migrationBuilder.DropColumn(
                name: "SweepSavingsPercent",
                table: "Households");

            migrationBuilder.DropColumn(
                name: "SweepTarget",
                table: "Households");

            migrationBuilder.DropColumn(
                name: "ClosedUtc",
                table: "BudgetMonths");

            migrationBuilder.AddColumn<long>(
                name: "SweepAmountCents",
                table: "BudgetMonths",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SweepDestination",
                table: "BudgetMonths",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }
    }
}
