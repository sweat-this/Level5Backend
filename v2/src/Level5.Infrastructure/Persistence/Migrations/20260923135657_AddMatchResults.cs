using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Level5.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "match_results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientResultId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LevelId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CharacterId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ClientVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Platform = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MetricsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ModifiersJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_match_results", x => x.Id);
                    table.ForeignKey(
                        name: "FK_match_results_player_profiles_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "player_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_match_results_PlayerId_ClientResultId",
                table: "match_results",
                columns: new[] { "PlayerId", "ClientResultId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "match_results");
        }
    }
}
