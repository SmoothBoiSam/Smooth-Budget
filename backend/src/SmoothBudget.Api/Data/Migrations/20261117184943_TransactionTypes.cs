using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TransactionTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CategoryRules");

            migrationBuilder.AddColumn<Guid>(
                name: "TransferAccountId",
                table: "Transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_TransferAccountId",
                table: "Transactions",
                column: "TransferAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_Transactions_Accounts_TransferAccountId",
                table: "Transactions",
                column: "TransferAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Transactions_Accounts_TransferAccountId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_TransferAccountId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "TransferAccountId",
                table: "Transactions");

            migrationBuilder.CreateTable(
                name: "CategoryRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uuid", nullable: false),
                    Pattern = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategoryRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CategoryRules_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CategoryRules_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CategoryRules_CategoryId",
                table: "CategoryRules",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_CategoryRules_HouseholdId_Pattern",
                table: "CategoryRules",
                columns: new[] { "HouseholdId", "Pattern" },
                unique: true);
        }
    }
}
