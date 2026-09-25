using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ButlerDidIt.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MediaPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentType",
                table: "MediaAssets",
                type: "character varying(60)",
                maxLength: 60,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "SizeBytes",
                table: "MediaAssets",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<decimal>(
                name: "PerRequest",
                table: "AiModelPrices",
                type: "numeric(12,4)",
                precision: 12,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "MediaJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    HostUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Total = table.Column<int>(type: "integer", nullable: false),
                    Done = table.Column<int>(type: "integer", nullable: false),
                    Failed = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScenarioMedia",
                columns: table => new
                {
                    ScenarioId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScenarioMedia", x => new { x.ScenarioId, x.Key });
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaJobs_ScenarioId",
                table: "MediaJobs",
                column: "ScenarioId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaJobs_Status_CreatedAt",
                table: "MediaJobs",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaJobs");

            migrationBuilder.DropTable(
                name: "ScenarioMedia");

            migrationBuilder.DropColumn(
                name: "ContentType",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "SizeBytes",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "PerRequest",
                table: "AiModelPrices");
        }
    }
}
