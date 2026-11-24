using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SavingsSplit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SavingsPercent",
                table: "Accounts",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SavingsPercent",
                table: "Accounts");
        }
    }
}
