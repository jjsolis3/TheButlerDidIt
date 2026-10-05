using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ButlerDidIt.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class HostPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Preferences",
                table: "AspNetUsers",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Preferences",
                table: "AspNetUsers");
        }
    }
}
