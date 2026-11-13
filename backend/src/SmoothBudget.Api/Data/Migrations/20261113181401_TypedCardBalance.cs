using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TypedCardBalance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TypedBalanceCents",
                table: "DebtAccounts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "TypedOn",
                table: "DebtAccounts",
                type: "date",
                nullable: true);

            // DECISIONS.md 253: a card that isn't bank-linked keeps the balance it shows today as "typed today" (in the
            // household's time zone), and follows its payments and charges from tomorrow on. When it was really typed isn't
            // recorded, so nothing shown changes until you type it again.
            migrationBuilder.Sql("""
                UPDATE "DebtAccounts" d
                SET "TypedBalanceCents" = d."BalanceCents",
                    "TypedOn" = (now() AT TIME ZONE COALESCE((SELECT z.name FROM pg_timezone_names z WHERE z.name = h."TimeZoneId"), 'UTC'))::date
                FROM "Accounts" a, "Households" h
                WHERE a."Id" = d."AccountId" AND h."Id" = d."HouseholdId" AND a."PlaidAccountId" IS NULL AND d."BalanceCents" > 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TypedBalanceCents",
                table: "DebtAccounts");

            migrationBuilder.DropColumn(
                name: "TypedOn",
                table: "DebtAccounts");
        }
    }
}
