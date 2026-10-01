using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ButlerDidIt.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccessGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FreeAccess",
                table: "Invites",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AccessGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Games = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessGrants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessGrants_UserId",
                table: "AccessGrants",
                column: "UserId");

            // Hosts who had an account before plans keep both games, free for good (owner's decision, #100).
            // Games = 3 is GameAccess.Both. New hosts get a free trial when they sign up instead.
            migrationBuilder.Sql("""
                INSERT INTO "AccessGrants" ("Id", "UserId", "Games", "Kind", "StartsAt", "EndsAt", "RevokedAt", "Note", "CreatedAt")
                SELECT gen_random_uuid(), "Id", 3, 'Comp', now(), NULL, NULL, 'Had an account before plans', now()
                FROM "AspNetUsers";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessGrants");

            migrationBuilder.DropColumn(
                name: "FreeAccess",
                table: "Invites");
        }
    }
}
