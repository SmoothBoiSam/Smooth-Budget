using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BalanceAsOf : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "BalanceAsOf",
                table: "Accounts",
                type: "date",
                nullable: true);

            // A balance already given (by a CSV or typed in) stays as it is: it counts as true today, so only later
            // transactions move it. A balance never given (still 0) is left without a date, so its transactions add up
            // to it (DECISIONS.md 189).
            migrationBuilder.Sql(
                """UPDATE "Accounts" SET "BalanceAsOf" = CURRENT_DATE WHERE "PlaidAccountId" IS NULL AND "CurrentBalanceCents" <> 0;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BalanceAsOf",
                table: "Accounts");
        }
    }
}
