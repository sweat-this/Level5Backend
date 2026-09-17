using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Level5.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesCreateIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClientRequestId",
                table: "competitive_series",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_competitive_series_ChallengerId_ClientRequestId",
                table: "competitive_series",
                columns: new[] { "ChallengerId", "ClientRequestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_competitive_series_ChallengerId_ClientRequestId",
                table: "competitive_series");

            migrationBuilder.DropColumn(
                name: "ClientRequestId",
                table: "competitive_series");
        }
    }
}
