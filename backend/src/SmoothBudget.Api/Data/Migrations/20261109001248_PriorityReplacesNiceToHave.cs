using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <summary>
    /// Category priority replaces "nice-to-have" (DECISIONS.md 182): nice-to-have categories become low priority (0),
    /// every other one medium (1).
    /// </summary>
    public partial class PriorityReplacesNiceToHave : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "Categories",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.Sql("UPDATE \"Categories\" SET \"Priority\" = 0 WHERE \"IsDiscretionary\";");

            migrationBuilder.DropColumn(
                name: "IsDiscretionary",
                table: "Categories");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDiscretionary",
                table: "Categories",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE \"Categories\" SET \"IsDiscretionary\" = TRUE WHERE \"Priority\" = 0;");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "Categories");
        }
    }
}
