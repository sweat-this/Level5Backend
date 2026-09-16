using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Level5.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UsernameCanonical = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "competitive_series",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengerId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpponentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    TotalGames = table.Column<int>(type: "integer", nullable: false),
                    CurrentGameNumber = table.Column<int>(type: "integer", nullable: false),
                    WinnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StateJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_competitive_series", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "friend_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FromPlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToPlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RespondedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_friend_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "friendships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LowerPlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpperPlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_friendships", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "player_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Tag = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_profiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_accounts_UsernameCanonical",
                table: "accounts",
                column: "UsernameCanonical",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_competitive_series_ChallengerId_Status",
                table: "competitive_series",
                columns: new[] { "ChallengerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_competitive_series_OpponentId_Status",
                table: "competitive_series",
                columns: new[] { "OpponentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_friend_requests_FromPlayerId_Status",
                table: "friend_requests",
                columns: new[] { "FromPlayerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_friend_requests_FromPlayerId_ToPlayerId",
                table: "friend_requests",
                columns: new[] { "FromPlayerId", "ToPlayerId" },
                unique: true,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_friend_requests_ToPlayerId_Status",
                table: "friend_requests",
                columns: new[] { "ToPlayerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_friendships_LowerPlayerId_UpperPlayerId",
                table: "friendships",
                columns: new[] { "LowerPlayerId", "UpperPlayerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_player_profiles_AccountId",
                table: "player_profiles",
                column: "AccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_player_profiles_Tag",
                table: "player_profiles",
                column: "Tag",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "competitive_series");

            migrationBuilder.DropTable(
                name: "friend_requests");

            migrationBuilder.DropTable(
                name: "friendships");

            migrationBuilder.DropTable(
                name: "player_profiles");
        }
    }
}
