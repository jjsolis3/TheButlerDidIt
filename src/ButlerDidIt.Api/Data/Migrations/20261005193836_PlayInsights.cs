using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ButlerDidIt.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlayInsights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlayFeedback",
                columns: table => new
                {
                    PartyId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeatId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ContentId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    HostUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: false),
                    Difficulty = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Comment = table.Column<string>(type: "character varying(280)", maxLength: 280, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayFeedback", x => new { x.PartyId, x.SeatId });
                });

            migrationBuilder.CreateTable(
                name: "PlayRecords",
                columns: table => new
                {
                    PartyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ContentId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    HostUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PlayerCount = table.Column<int>(type: "integer", nullable: false),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    Accusers = table.Column<int>(type: "integer", nullable: true),
                    Correct = table.Column<int>(type: "integer", nullable: true),
                    Escaped = table.Column<bool>(type: "boolean", nullable: true),
                    Difficulty = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Minutes = table.Column<int>(type: "integer", nullable: true),
                    HintsUsed = table.Column<int>(type: "integer", nullable: true),
                    Details = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayRecords", x => x.PartyId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayFeedback_HostUserId",
                table: "PlayFeedback",
                column: "HostUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PlayFeedback_Kind_ContentId",
                table: "PlayFeedback",
                columns: new[] { "Kind", "ContentId" });

            migrationBuilder.CreateIndex(
                name: "IX_PlayRecords_HostUserId",
                table: "PlayRecords",
                column: "HostUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PlayRecords_Kind_ContentId",
                table: "PlayRecords",
                columns: new[] { "Kind", "ContentId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayFeedback");

            migrationBuilder.DropTable(
                name: "PlayRecords");
        }
    }
}
