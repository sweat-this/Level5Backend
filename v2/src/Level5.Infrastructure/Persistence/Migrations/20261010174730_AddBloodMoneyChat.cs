using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Level5.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBloodMoneyChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blood_money_chat_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    SenderPlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Visibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blood_money_chat_messages", x => x.Id);
                    table.CheckConstraint("CK_blood_chat_body", "octet_length(\"Body\") BETWEEN 1 AND 2000");
                    table.CheckConstraint("CK_blood_chat_identity", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"ClientMessageId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_blood_chat_sequence", "\"Sequence\" > 0");
                    table.CheckConstraint("CK_blood_chat_visibility", "\"Visibility\" IN ('Visible', 'Suppressed')");
                    table.ForeignKey(
                        name: "FK_blood_money_chat_messages_blood_money_challenge_participant~",
                        columns: x => new { x.ChallengeId, x.SenderPlayerId },
                        principalTable: "blood_money_challenge_participants",
                        principalColumns: new[] { "ChallengeId", "PlayerId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "blood_money_chat_participant_state",
                columns: table => new
                {
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastReadSequence = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    NotificationsMuted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blood_money_chat_participant_state", x => new { x.ChallengeId, x.PlayerId });
                    table.CheckConstraint("CK_blood_chat_read_sequence", "\"LastReadSequence\" >= 0");
                    table.ForeignKey(
                        name: "FK_blood_money_chat_participant_state_blood_money_challenge_pa~",
                        columns: x => new { x.ChallengeId, x.PlayerId },
                        principalTable: "blood_money_challenge_participants",
                        principalColumns: new[] { "ChallengeId", "PlayerId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_blood_money_chat_messages_ChallengeId_SenderPlayerId_Client~",
                table: "blood_money_chat_messages",
                columns: new[] { "ChallengeId", "SenderPlayerId", "ClientMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_blood_money_chat_messages_ChallengeId_SenderPlayerId_Create~",
                table: "blood_money_chat_messages",
                columns: new[] { "ChallengeId", "SenderPlayerId", "CreatedAt", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_blood_money_chat_messages_ChallengeId_Sequence",
                table: "blood_money_chat_messages",
                columns: new[] { "ChallengeId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blood_money_chat_messages");

            migrationBuilder.DropTable(
                name: "blood_money_chat_participant_state");
        }
    }
}
