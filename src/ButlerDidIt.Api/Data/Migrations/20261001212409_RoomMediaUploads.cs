using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ButlerDidIt.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RoomMediaUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "MediaAssets",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScenarioMedia_AssetId",
                table: "ScenarioMedia",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_OwnerUserId",
                table: "MediaAssets",
                column: "OwnerUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ScenarioMedia_AssetId",
                table: "ScenarioMedia");

            migrationBuilder.DropIndex(
                name: "IX_MediaAssets_OwnerUserId",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "MediaAssets");
        }
    }
}
