using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveAndSameBill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MergedIntoKey",
                table: "RecurringItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotSameAsKey",
                table: "RecurringItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedUtc",
                table: "Categories",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "KeptUnusedUtc",
                table: "Categories",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MergedIntoKey",
                table: "RecurringItems");

            migrationBuilder.DropColumn(
                name: "NotSameAsKey",
                table: "RecurringItems");

            migrationBuilder.DropColumn(
                name: "CreatedUtc",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "KeptUnusedUtc",
                table: "Categories");
        }
    }
}
