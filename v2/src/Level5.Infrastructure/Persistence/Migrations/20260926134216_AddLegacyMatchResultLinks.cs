using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Level5.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLegacyMatchResultLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "legacy_match_result_links",
                columns: table => new
                {
                    LegacyHighscoreId = table.Column<int>(type: "integer", nullable: false),
                    MatchResultId = table.Column<Guid>(type: "uuid", nullable: false),
                    LegacyScoreId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    MigratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legacy_match_result_links", x => x.LegacyHighscoreId);
                    table.ForeignKey(
                        name: "FK_legacy_match_result_links_match_results_MatchResultId",
                        column: x => x.MatchResultId,
                        principalTable: "match_results",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_legacy_match_result_links_MatchResultId",
                table: "legacy_match_result_links",
                column: "MatchResultId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "legacy_match_result_links");
        }
    }
}
