using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ButlerDidIt.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TermsAccepted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TermsAcceptedAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TermsAcceptedAt",
                table: "AspNetUsers");
        }
    }
}
