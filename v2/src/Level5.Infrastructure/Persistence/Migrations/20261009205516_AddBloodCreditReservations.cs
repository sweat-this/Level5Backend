using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Level5.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBloodCreditReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_blood_credit_kind",
                table: "blood_money_credit_transactions");

            migrationBuilder.CreateTable(
                name: "blood_money_credit_reservations",
                columns: table => new
                {
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReserveTransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseTransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleaseReason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blood_money_credit_reservations", x => new { x.ChallengeId, x.PlayerId });
                    table.CheckConstraint("CK_blood_reservation_amount", "\"Amount\" > 0");
                    table.CheckConstraint("CK_blood_reservation_identity", "\"ChallengeId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"PlayerId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_blood_reservation_revision", "\"Revision\" > 0");
                    table.CheckConstraint("CK_blood_reservation_state", "(\"Status\" = 'Reserved' AND \"ReleaseTransactionId\" IS NULL AND \"ReleasedAt\" IS NULL AND \"ReleaseReason\" IS NULL)\r\nOR (\"Status\" = 'Released' AND \"ReleaseTransactionId\" IS NOT NULL AND \"ReleasedAt\" IS NOT NULL\r\n    AND \"ReleasedAt\" >= \"ReservedAt\" AND \"ReleaseReason\" IS NOT NULL\r\n    AND \"ReleaseReason\" IN ('Declined', 'Cancelled', 'PendingExpired') AND \"ReleaseTransactionId\" <> \"ReserveTransactionId\")");
                    table.ForeignKey(
                        name: "FK_blood_money_credit_reservations_blood_money_credit_transact~",
                        column: x => x.ReleaseTransactionId,
                        principalTable: "blood_money_credit_transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_blood_money_credit_reservations_blood_money_credit_transac~1",
                        column: x => x.ReserveTransactionId,
                        principalTable: "blood_money_credit_transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_blood_money_credit_reservations_player_profiles_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "player_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_blood_credit_kind",
                table: "blood_money_credit_transactions",
                sql: "(\"Kind\" IN ('Issuance', 'Release') AND \"PlayerDelta\" > 0) OR (\"Kind\" IN ('Spend', 'Reserve') AND \"PlayerDelta\" < 0) OR \"Kind\" = 'Correction'");

            migrationBuilder.CreateIndex(
                name: "IX_blood_money_credit_reservations_PlayerId",
                table: "blood_money_credit_reservations",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_blood_money_credit_reservations_ReleaseTransactionId",
                table: "blood_money_credit_reservations",
                column: "ReleaseTransactionId",
                unique: true,
                filter: "\"ReleaseTransactionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_blood_money_credit_reservations_ReserveTransactionId",
                table: "blood_money_credit_reservations",
                column: "ReserveTransactionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blood_money_credit_reservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_blood_credit_kind",
                table: "blood_money_credit_transactions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_blood_credit_kind",
                table: "blood_money_credit_transactions",
                sql: "(\"Kind\" = 'Issuance' AND \"PlayerDelta\" > 0) OR (\"Kind\" = 'Spend' AND \"PlayerDelta\" < 0) OR \"Kind\" = 'Correction'");
        }
    }
}
