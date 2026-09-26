using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ButlerDidIt.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DealAtStart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DealAtStart",
                table: "Parties",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TailorWithAi",
                table: "Parties",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "GenerationJobs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Mystery"); // existing jobs were all new mysteries

            migrationBuilder.AddColumn<Guid>(
                name: "PartyId",
                table: "GenerationJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceScenarioId",
                table: "GenerationJobs",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetCharacterId",
                table: "GenerationJobs",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DealAtStart",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "TailorWithAi",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "GenerationJobs");

            migrationBuilder.DropColumn(
                name: "PartyId",
                table: "GenerationJobs");

            migrationBuilder.DropColumn(
                name: "SourceScenarioId",
                table: "GenerationJobs");

            migrationBuilder.DropColumn(
                name: "TargetCharacterId",
                table: "GenerationJobs");
        }
    }
}
