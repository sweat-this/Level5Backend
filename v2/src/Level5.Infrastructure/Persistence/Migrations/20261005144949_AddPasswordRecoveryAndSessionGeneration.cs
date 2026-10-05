using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Level5.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordRecoveryAndSessionGeneration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SessionGeneration",
                table: "auth_sessions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SessionGeneration",
                table: "accounts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "password_reset_challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    TargetEmailCanonical = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_password_reset_challenges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_password_reset_challenges_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_challenges_AccountId",
                table: "password_reset_challenges",
                column: "AccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_challenges_TokenHash",
                table: "password_reset_challenges",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "password_reset_challenges");

            migrationBuilder.DropColumn(
                name: "SessionGeneration",
                table: "auth_sessions");

            migrationBuilder.DropColumn(
                name: "SessionGeneration",
                table: "accounts");
        }
    }
}
