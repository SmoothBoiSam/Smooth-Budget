using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountNickname : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Nickname",
                table: "Accounts",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Nickname",
                table: "Accounts");
        }
    }
}
