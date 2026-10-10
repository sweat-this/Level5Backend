using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Level5.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBloodMoneyChallenges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blood_money_challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatorPlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    StakePerParticipant = table.Column<long>(type: "bigint", nullable: false),
                    RulesetId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RulesetVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcceptanceDeadlineAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    GameplayDeadlineAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TerminalAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TerminalActorPlayerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blood_money_challenges", x => x.Id);
                    table.CheckConstraint("CK_blood_challenge_deadline", "\"AcceptanceDeadlineAt\" > \"CreatedAt\"");
                    table.CheckConstraint("CK_blood_challenge_identity", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"ClientRequestId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_blood_challenge_state", "(\"Status\" = 'PendingAcceptance' AND \"ActivatedAt\" IS NULL AND \"GameplayDeadlineAt\" IS NULL AND \"TerminalAt\" IS NULL AND \"TerminalActorPlayerId\" IS NULL)\nOR (\"Status\" = 'Active' AND \"ActivatedAt\" IS NOT NULL AND \"ActivatedAt\" >= \"CreatedAt\" AND \"ActivatedAt\" < \"AcceptanceDeadlineAt\"\n    AND \"GameplayDeadlineAt\" IS NOT NULL AND \"GameplayDeadlineAt\" > \"ActivatedAt\" AND \"TerminalAt\" IS NULL AND \"TerminalActorPlayerId\" IS NULL)\nOR (\"Status\" IN ('Declined', 'Cancelled', 'Expired') AND \"ActivatedAt\" IS NULL AND \"GameplayDeadlineAt\" IS NULL\n    AND \"TerminalAt\" IS NOT NULL AND \"TerminalAt\" >= \"CreatedAt\"\n    AND ((\"Status\" = 'Expired' AND \"TerminalAt\" >= \"AcceptanceDeadlineAt\" AND \"TerminalActorPlayerId\" IS NULL)\n        OR (\"Status\" IN ('Declined', 'Cancelled') AND \"TerminalAt\" < \"AcceptanceDeadlineAt\" AND \"TerminalActorPlayerId\" IS NOT NULL\n            AND (\"Status\" <> 'Cancelled' OR \"TerminalActorPlayerId\" = \"CreatorPlayerId\"))))");
                    table.CheckConstraint("CK_blood_challenge_values", "\"StakePerParticipant\" > 0 AND \"RulesetVersion\" > 0 AND \"Revision\" > 0 AND length(btrim(\"RulesetId\")) > 0 AND \"RulesetId\" = btrim(\"RulesetId\")");
                    table.ForeignKey(
                        name: "FK_blood_money_challenges_player_profiles_CreatorPlayerId",
                        column: x => x.CreatorPlayerId,
                        principalTable: "player_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_blood_money_challenges_player_profiles_TerminalActorPlayerId",
                        column: x => x.TerminalActorPlayerId,
                        principalTable: "player_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "blood_money_challenge_participants",
                columns: table => new
                {
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeatIndex = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blood_money_challenge_participants", x => new { x.ChallengeId, x.PlayerId });
                    table.CheckConstraint("CK_blood_participant_seat", "\"SeatIndex\" >= 0");
                    table.CheckConstraint("CK_blood_participant_state", "(\"Status\" = 'Accepted' AND \"AcceptedAt\" IS NOT NULL) OR (\"Status\" IN ('Invited', 'Declined') AND \"AcceptedAt\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_blood_money_challenge_participants_blood_money_challenges_C~",
                        column: x => x.ChallengeId,
                        principalTable: "blood_money_challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_blood_money_challenge_participants_player_profiles_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "player_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_blood_money_challenge_participants_ChallengeId_SeatIndex",
                table: "blood_money_challenge_participants",
                columns: new[] { "ChallengeId", "SeatIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_blood_money_challenge_participants_PlayerId",
                table: "blood_money_challenge_participants",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_blood_money_challenges_CreatorPlayerId_ClientRequestId",
                table: "blood_money_challenges",
                columns: new[] { "CreatorPlayerId", "ClientRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_blood_money_challenges_TerminalActorPlayerId",
                table: "blood_money_challenges",
                column: "TerminalActorPlayerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blood_money_challenge_participants");

            migrationBuilder.DropTable(
                name: "blood_money_challenges");
        }
    }
}
