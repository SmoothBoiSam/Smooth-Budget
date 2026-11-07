using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecurringItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecurringItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: true),
                    DueDay = table.Column<int>(type: "integer", nullable: true),
                    Confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    Dismissed = table.Column<bool>(type: "boolean", nullable: false),
                    CanceledOn = table.Column<DateOnly>(type: "date", nullable: true),
                    AcknowledgedAmountCents = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringItems_HouseholdId_Key",
                table: "RecurringItems",
                columns: new[] { "HouseholdId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecurringItems");
        }
    }
}
