using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountClosing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "ClosedOn",
                table: "Accounts",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReplacedById",
                table: "Accounts",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClosedOn",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "ReplacedById",
                table: "Accounts");
        }
    }
}
