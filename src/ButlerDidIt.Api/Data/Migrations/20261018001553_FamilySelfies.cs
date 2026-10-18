using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ButlerDidIt.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class FamilySelfies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SelfiesAllowed",
                table: "Parties",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SelfiesConsentAt",
                table: "Parties",
                type: "timestamp with time zone",
                nullable: true);

            // Family parties still to be played start with selfies off, like new ones: the host turns them on once the
            // parents are OK. A selfie already taken stays until its guest removes it or the host turns selfies off.
            migrationBuilder.Sql("""
                UPDATE "Parties" SET "SelfiesAllowed" = false WHERE "ContentLevel" = 'Family' AND "Status" <> 'Finished';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SelfiesAllowed",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "SelfiesConsentAt",
                table: "Parties");
        }
    }
}
