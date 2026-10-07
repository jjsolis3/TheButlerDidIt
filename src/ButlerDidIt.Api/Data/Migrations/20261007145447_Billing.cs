using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ButlerDidIt.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Billing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BillingCustomerId",
                table: "AspNetUsers",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "AccessGrants",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RenewsAt",
                table: "AccessGrants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "AccessGrants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BillingEvents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CustomerId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_BillingCustomerId",
                table: "AspNetUsers",
                column: "BillingCustomerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessGrants_ExternalId",
                table: "AccessGrants",
                column: "ExternalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingEvents_ReceivedAt",
                table: "BillingEvents",
                column: "ReceivedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingEvents");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_BillingCustomerId",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_AccessGrants_ExternalId",
                table: "AccessGrants");

            migrationBuilder.DropColumn(
                name: "BillingCustomerId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "AccessGrants");

            migrationBuilder.DropColumn(
                name: "RenewsAt",
                table: "AccessGrants");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "AccessGrants");
        }
    }
}
