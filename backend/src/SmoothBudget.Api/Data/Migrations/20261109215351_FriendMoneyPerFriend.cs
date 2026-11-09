using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothBudget.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class FriendMoneyPerFriend : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FriendId",
                table: "Transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FriendId",
                table: "PartnerArrangements",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateOnly>(
                name: "LabelsStartAfter",
                table: "Friends",
                type: "date",
                nullable: true);

            // What was the partner's moves onto the friend who now holds that transfer name (FriendTransfers made one from
            // it; DECISIONS.md 207, 211): when labeling started, her fixed contributions, and every transfer between you.
            // A household with contributions but no such friend gets one named "Friend", so nothing is lost.
            migrationBuilder.Sql("""
                UPDATE "Friends" f SET "LabelsStartAfter" = h."PartnerLabelsStartAfter"
                FROM "Households" h
                WHERE f."HouseholdId" = h."Id" AND f."TransferName" IS NOT NULL;

                INSERT INTO "Friends" ("Id", "HouseholdId", "Name", "LinkId", "CreatedUtc", "TransferName", "LabelsStartAfter")
                SELECT gen_random_uuid(), h."Id", 'Friend', NULL, now(), NULL, NULL
                FROM "Households" h
                WHERE EXISTS (SELECT 1 FROM "PartnerArrangements" a WHERE a."HouseholdId" = h."Id")
                  AND NOT EXISTS (SELECT 1 FROM "Friends" g WHERE g."HouseholdId" = h."Id" AND (g."TransferName" IS NOT NULL OR lower(g."Name") = 'friend'));

                UPDATE "PartnerArrangements" a SET "FriendId" = coalesce(
                    (SELECT g."Id" FROM "Friends" g WHERE g."HouseholdId" = a."HouseholdId" AND g."TransferName" IS NOT NULL ORDER BY g."CreatedUtc" LIMIT 1),
                    (SELECT g."Id" FROM "Friends" g WHERE g."HouseholdId" = a."HouseholdId" AND lower(g."Name") = 'friend' ORDER BY g."CreatedUtc" LIMIT 1));

                UPDATE "Transactions" t SET "FriendId" =
                    (SELECT g."Id" FROM "Friends" g WHERE g."HouseholdId" = t."HouseholdId" AND g."TransferName" IS NOT NULL ORDER BY g."CreatedUtc" LIMIT 1)
                WHERE t."IsPartnerTransfer";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_FriendId",
                table: "Transactions",
                column: "FriendId");

            migrationBuilder.CreateIndex(
                name: "IX_PartnerArrangements_FriendId",
                table: "PartnerArrangements",
                column: "FriendId");

            migrationBuilder.AddForeignKey(
                name: "FK_PartnerArrangements_Friends_FriendId",
                table: "PartnerArrangements",
                column: "FriendId",
                principalTable: "Friends",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Transactions_Friends_FriendId",
                table: "Transactions",
                column: "FriendId",
                principalTable: "Friends",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PartnerArrangements_Friends_FriendId",
                table: "PartnerArrangements");

            migrationBuilder.DropForeignKey(
                name: "FK_Transactions_Friends_FriendId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_FriendId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_PartnerArrangements_FriendId",
                table: "PartnerArrangements");

            migrationBuilder.DropColumn(
                name: "FriendId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "FriendId",
                table: "PartnerArrangements");

            migrationBuilder.DropColumn(
                name: "LabelsStartAfter",
                table: "Friends");
        }
    }
}
