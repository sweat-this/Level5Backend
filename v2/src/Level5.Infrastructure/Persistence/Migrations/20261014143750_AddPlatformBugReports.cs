using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Level5.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformBugReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bug_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reporter_player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    game_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    steps_to_reproduce = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    game_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bug_reports", x => x.id);
                    table.ForeignKey(
                        name: "FK_bug_reports_player_profiles_reporter_player_id",
                        column: x => x.reporter_player_id,
                        principalTable: "player_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bug_reports_reporter_player_id_client_submission_id",
                table: "bug_reports",
                columns: new[] { "reporter_player_id", "client_submission_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bug_reports");
        }
    }
}
