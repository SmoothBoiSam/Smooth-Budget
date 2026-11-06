using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DebtAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DebtStrategy",
                table: "Households",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "ExtraDebtPaymentCents",
                table: "Households",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CreditLimitCents",
                table: "Accounts",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DebtAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    BalanceCents = table.Column<long>(type: "bigint", nullable: false),
                    AprBps = table.Column<int>(type: "integer", nullable: true),
                    MinimumPaymentCents = table.Column<long>(type: "bigint", nullable: true),
                    DueDay = table.Column<int>(type: "integer", nullable: true),
                    CreditLimitCents = table.Column<long>(type: "bigint", nullable: true),
                    StartingBalanceCents = table.Column<long>(type: "bigint", nullable: false),
                    StartedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    FromBank = table.Column<bool>(type: "boolean", nullable: false),
                    IsHidden = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DebtAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DebtAccounts_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DebtAccounts_AccountId",
                table: "DebtAccounts",
                column: "AccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DebtAccounts_HouseholdId",
                table: "DebtAccounts",
                column: "HouseholdId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DebtAccounts");

            migrationBuilder.DropColumn(
                name: "DebtStrategy",
                table: "Households");

            migrationBuilder.DropColumn(
                name: "ExtraDebtPaymentCents",
                table: "Households");

            migrationBuilder.DropColumn(
                name: "CreditLimitCents",
                table: "Accounts");
        }
    }
}
