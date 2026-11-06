using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Plaid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PlaidTransactionId",
                table: "Transactions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlaidAccountId",
                table: "Accounts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlaidItemId",
                table: "Accounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlaidItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlaidItemId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    InstitutionId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    InstitutionName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AccessTokenEncrypted = table.Column<string>(type: "text", nullable: false),
                    Cursor = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StatusMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastSyncedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAttemptUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaidItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaidItems_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_PlaidTransactionId",
                table: "Transactions",
                column: "PlaidTransactionId",
                unique: true,
                filter: "\"PlaidTransactionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_PlaidAccountId",
                table: "Accounts",
                column: "PlaidAccountId",
                unique: true,
                filter: "\"PlaidAccountId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_PlaidItemId",
                table: "Accounts",
                column: "PlaidItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaidItems_HouseholdId",
                table: "PlaidItems",
                column: "HouseholdId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaidItems_PlaidItemId",
                table: "PlaidItems",
                column: "PlaidItemId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Accounts_PlaidItems_PlaidItemId",
                table: "Accounts",
                column: "PlaidItemId",
                principalTable: "PlaidItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Accounts_PlaidItems_PlaidItemId",
                table: "Accounts");

            migrationBuilder.DropTable(
                name: "PlaidItems");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_PlaidTransactionId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_PlaidAccountId",
                table: "Accounts");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_PlaidItemId",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "PlaidTransactionId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "PlaidAccountId",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "PlaidItemId",
                table: "Accounts");
        }
    }
}
