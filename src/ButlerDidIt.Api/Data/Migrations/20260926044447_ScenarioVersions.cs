using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ButlerDidIt.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScenarioVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VariantOf",
                table: "Scenarios",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Scenarios_VariantOf",
                table: "Scenarios",
                column: "VariantOf");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Scenarios_VariantOf",
                table: "Scenarios");

            migrationBuilder.DropColumn(
                name: "VariantOf",
                table: "Scenarios");
        }
    }
}
