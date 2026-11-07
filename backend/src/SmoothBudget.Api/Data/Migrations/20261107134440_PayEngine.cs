using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PayEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaydayChecklistItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaydayChecklistItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayPeriodForecasts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    PredictedGrossCents = table.Column<long>(type: "bigint", nullable: false),
                    PredictedNetCents = table.Column<long>(type: "bigint", nullable: false),
                    LowNetCents = table.Column<long>(type: "bigint", nullable: false),
                    HighNetCents = table.Column<long>(type: "bigint", nullable: false),
                    ActualGrossCents = table.Column<long>(type: "bigint", nullable: true),
                    ActualNetCents = table.Column<long>(type: "bigint", nullable: true),
                    ActualSource = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    DepositTransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayPeriodForecasts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uuid", nullable: false),
                    YearsOfService = table.Column<int>(type: "integer", nullable: false),
                    FullTime = table.Column<bool>(type: "boolean", nullable: false),
                    HolidayPay = table.Column<bool>(type: "boolean", nullable: false),
                    FlatNetPercent = table.Column<int>(type: "integer", nullable: false),
                    RulesJson = table.Column<string>(type: "text", nullable: true),
                    ModelJson = table.Column<string>(type: "text", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayStubs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    GrossCents = table.Column<long>(type: "bigint", nullable: false),
                    NetCents = table.Column<long>(type: "bigint", nullable: false),
                    Json = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayStubs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleMonths",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Month = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Json = table.Column<string>(type: "text", nullable: false),
                    Reserve = table.Column<bool>(type: "boolean", nullable: false),
                    OvertimeKeys = table.Column<string>(type: "text", nullable: false),
                    PaidEntryKeys = table.Column<string>(type: "text", nullable: false),
                    ImportedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleMonths", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaydayChecklistItems_HouseholdId_PayDate_Key",
                table: "PaydayChecklistItems",
                columns: new[] { "HouseholdId", "PayDate", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayPeriodForecasts_HouseholdId_PayDate",
                table: "PayPeriodForecasts",
                columns: new[] { "HouseholdId", "PayDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayProfiles_HouseholdId",
                table: "PayProfiles",
                column: "HouseholdId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayStubs_HouseholdId_PayDate",
                table: "PayStubs",
                columns: new[] { "HouseholdId", "PayDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleMonths_HouseholdId_Year_Month",
                table: "ScheduleMonths",
                columns: new[] { "HouseholdId", "Year", "Month" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaydayChecklistItems");

            migrationBuilder.DropTable(
                name: "PayPeriodForecasts");

            migrationBuilder.DropTable(
                name: "PayProfiles");

            migrationBuilder.DropTable(
                name: "PayStubs");

            migrationBuilder.DropTable(
                name: "ScheduleMonths");
        }
    }
}
