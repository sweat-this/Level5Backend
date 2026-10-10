using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Level5.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBloodMoneyChatReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_blood_money_chat_messages_ChallengeId_Id",
                table: "blood_money_chat_messages",
                columns: new[] { "ChallengeId", "Id" });

            migrationBuilder.CreateTable(
                name: "blood_money_chat_reports",
                columns: table => new
                {
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReporterPlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blood_money_chat_reports", x => x.ReportId);
                    table.CheckConstraint("CK_blood_chat_report_identity", "\"ReportId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_blood_chat_report_reason", "\"Reason\" IN ('Harassment', 'Hate', 'Threat', 'SexualContent', 'Spam', 'Other')");
                    table.ForeignKey(
                        name: "FK_blood_money_chat_reports_blood_money_challenge_participants~",
                        columns: x => new { x.ChallengeId, x.ReporterPlayerId },
                        principalTable: "blood_money_challenge_participants",
                        principalColumns: new[] { "ChallengeId", "PlayerId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_blood_money_chat_reports_blood_money_chat_messages_Challeng~",
                        columns: x => new { x.ChallengeId, x.MessageId },
                        principalTable: "blood_money_chat_messages",
                        principalColumns: new[] { "ChallengeId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_blood_money_chat_reports_ChallengeId_MessageId_ReporterPlay~",
                table: "blood_money_chat_reports",
                columns: new[] { "ChallengeId", "MessageId", "ReporterPlayerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_blood_money_chat_reports_ChallengeId_ReporterPlayerId",
                table: "blood_money_chat_reports",
                columns: new[] { "ChallengeId", "ReporterPlayerId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blood_money_chat_reports");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_blood_money_chat_messages_ChallengeId_Id",
                table: "blood_money_chat_messages");
        }
    }
}
