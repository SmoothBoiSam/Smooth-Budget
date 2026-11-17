using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExpectedIncome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExpectedIncomes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AmountCents = table.Column<long>(type: "bigint", nullable: false),
                    ExpectedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DepositTransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpectedIncomes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpectedIncomes_HouseholdId_ExpectedDate",
                table: "ExpectedIncomes",
                columns: new[] { "HouseholdId", "ExpectedDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExpectedIncomes");
        }
    }
}
