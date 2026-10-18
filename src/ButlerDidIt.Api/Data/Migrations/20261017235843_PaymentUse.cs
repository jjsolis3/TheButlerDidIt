using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ButlerDidIt.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PaymentUse : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PeriodStart",
                table: "AccessGrants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GameStarts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HostUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PartyId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameStarts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameStarts_HostUserId_At",
                table: "GameStarts",
                columns: new[] { "HostUserId", "At" });

            // A pass's time began when it was paid for, which is when it starts. Subscriptions get theirs from Stripe at
            // their next sync (any webhook, or the admin's Sync button).
            migrationBuilder.Sql("""
                UPDATE "AccessGrants" SET "PeriodStart" = "StartsAt" WHERE "Kind" = 'Pass' AND "ExternalId" IS NOT NULL;
                """);

            // Games begun before this was recorded: when they began isn't known, so the party's creation stands in.
            // (Parties.Kind is a number: 1 is an escape room.)
            migrationBuilder.Sql("""
                INSERT INTO "GameStarts" ("Id", "HostUserId", "Kind", "PartyId", "At")
                SELECT gen_random_uuid(), "HostUserId", CASE "Kind" WHEN 1 THEN 'EscapeRoom' ELSE 'Mystery' END, "Id", "CreatedAt"
                FROM "Parties" WHERE "Status" <> 'Lobby';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameStarts");

            migrationBuilder.DropColumn(
                name: "PeriodStart",
                table: "AccessGrants");
        }
    }
}
