using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ButlerDidIt.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Retention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PrunedAt",
                table: "Parties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "Parties",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<Guid>(
                name: "PartyId",
                table: "MediaAssets",
                type: "uuid",
                nullable: true);

            // Existing rows: count every party as active "now", so upgrading doesn't make
            // the first retention run delete parties that are still in use.
            migrationBuilder.Sql("""UPDATE "Parties" SET "UpdatedAt" = now();""");

            // Link existing selfies to their party (found via the photo URL in the saved
            // game state), so they aren't mistaken for orphans and deleted.
            migrationBuilder.Sql("""
                UPDATE "MediaAssets" a SET "PartyId" = p."Id"
                FROM "Parties" p, jsonb_array_elements(p."State"->'players') pl
                WHERE a."Kind" = 'Photo' AND pl->>'photoUrl' = '/media/assets/' || a."Id"::text;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Parties_Status_UpdatedAt",
                table: "Parties",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_PartyId",
                table: "MediaAssets",
                column: "PartyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Parties_Status_UpdatedAt",
                table: "Parties");

            migrationBuilder.DropIndex(
                name: "IX_MediaAssets_PartyId",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "PrunedAt",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "PartyId",
                table: "MediaAssets");
        }
    }
}
