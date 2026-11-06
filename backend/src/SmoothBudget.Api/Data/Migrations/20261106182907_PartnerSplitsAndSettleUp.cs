using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartnerSplitsAndSettleUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPartnerTransfer",
                table: "Transactions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PartnerSettledThrough",
                table: "Households",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PartnerSharePercent",
                table: "Categories",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPartnerTransfer",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "PartnerSettledThrough",
                table: "Households");

            migrationBuilder.DropColumn(
                name: "PartnerSharePercent",
                table: "Categories");
        }
    }
}
