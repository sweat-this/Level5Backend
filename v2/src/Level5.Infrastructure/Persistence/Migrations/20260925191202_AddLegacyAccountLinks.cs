using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Level5.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLegacyAccountLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "legacy_account_links",
                columns: table => new
                {
                    LegacyUserId = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    LegacyUsername = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    MigratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legacy_account_links", x => x.LegacyUserId);
                    table.ForeignKey(
                        name: "FK_legacy_account_links_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_legacy_account_links_player_profiles_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "player_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_legacy_account_links_AccountId",
                table: "legacy_account_links",
                column: "AccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_legacy_account_links_PlayerId",
                table: "legacy_account_links",
                column: "PlayerId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "legacy_account_links");
        }
    }
}
