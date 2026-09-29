using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ButlerDidIt.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class EscapeResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EscapeResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RoomId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    PartyId = table.Column<Guid>(type: "uuid", nullable: false),
                    HostUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Seed = table.Column<long>(type: "bigint", nullable: false),
                    Daily = table.Column<bool>(type: "boolean", nullable: false),
                    Escaped = table.Column<bool>(type: "boolean", nullable: false),
                    ElapsedSeconds = table.Column<int>(type: "integer", nullable: false),
                    HintsUsed = table.Column<int>(type: "integer", nullable: false),
                    WrongAttempts = table.Column<int>(type: "integer", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    PlayerCount = table.Column<int>(type: "integer", nullable: false),
                    Team = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EscapeResults", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EscapeResults_PartyId",
                table: "EscapeResults",
                column: "PartyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EscapeResults_RoomId_Escaped_Score",
                table: "EscapeResults",
                columns: new[] { "RoomId", "Escaped", "Score" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EscapeResults");
        }
    }
}
