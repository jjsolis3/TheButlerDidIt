using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ButlerDidIt.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdminHub : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            // Accounts made before the date was kept: the earliest sign of them there is, their first grant (a trial
            // starts at sign-up) or party. LEAST skips NULLs, so either one is enough; neither leaves it NULL.
            migrationBuilder.Sql("""
                UPDATE "AspNetUsers" u SET "CreatedAt" = LEAST(
                    (SELECT MIN(g."CreatedAt") FROM "AccessGrants" g WHERE g."UserId" = u."Id"),
                    (SELECT MIN(p."CreatedAt") FROM "Parties" p WHERE p."HostUserId" = u."Id"));
                """);

            migrationBuilder.CreateTable(
                name: "SiteSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    AllowRegistration = table.Column<bool>(type: "boolean", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SiteSettings");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "AspNetUsers");
        }
    }
}
