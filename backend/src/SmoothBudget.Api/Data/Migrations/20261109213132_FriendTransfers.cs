using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class FriendTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TransferName",
                table: "Friends",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            // Partner becomes a friend (DECISIONS.md 207): the name on her transfers moves onto a friend named by its first
            // word ("JANE DOE" -> "Jane"). A friend you already added by that name takes it; otherwise one is added. Her
            // fixed contributions and transfers stay as they are: they're the household's, shown on that friend's page.
            migrationBuilder.Sql("""
                UPDATE "Friends" f SET "TransferName" = h."PartnerName"
                FROM "Households" h
                WHERE f."HouseholdId" = h."Id" AND h."PartnerName" IS NOT NULL
                  AND lower(f."Name") = lower(initcap(split_part(btrim(h."PartnerName"), ' ', 1)))
                  AND NOT EXISTS (SELECT 1 FROM "Friends" g WHERE g."HouseholdId" = h."Id" AND g."TransferName" IS NOT NULL);

                INSERT INTO "Friends" ("Id", "HouseholdId", "Name", "LinkId", "CreatedUtc", "TransferName")
                SELECT gen_random_uuid(), h."Id", left(initcap(split_part(btrim(h."PartnerName"), ' ', 1)), 60), NULL, now(), h."PartnerName"
                FROM "Households" h
                WHERE h."PartnerName" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM "Friends" g WHERE g."HouseholdId" = h."Id" AND g."TransferName" IS NOT NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TransferName",
                table: "Friends");
        }
    }
}
