using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BankChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "BankCheckMonth",
                table: "PlaidItems",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BankChecksThisMonth",
                table: "PlaidItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastBankCheckUtc",
                table: "PlaidItems",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BankCheckMonth",
                table: "PlaidItems");

            migrationBuilder.DropColumn(
                name: "BankChecksThisMonth",
                table: "PlaidItems");

            migrationBuilder.DropColumn(
                name: "LastBankCheckUtc",
                table: "PlaidItems");
        }
    }
}
