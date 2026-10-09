using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Level5.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBloodCreditLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blood_money_credit_accounts",
                columns: table => new
                {
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AvailableBalance = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blood_money_credit_accounts", x => x.PlayerId);
                    table.CheckConstraint("CK_blood_credit_balance", "\"AvailableBalance\" >= 0");
                    table.CheckConstraint("CK_blood_credit_revision", "\"Revision\" >= 0");
                    table.ForeignKey(
                        name: "FK_blood_money_credit_accounts_player_profiles_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "player_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "blood_money_credit_transactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SubjectPlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerDelta = table.Column<long>(type: "bigint", nullable: false),
                    ReferenceCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blood_money_credit_transactions", x => x.Id);
                    table.CheckConstraint("CK_blood_credit_delta", "\"PlayerDelta\" <> 0 AND \"PlayerDelta\" <> '-9223372036854775808'::bigint");
                    table.CheckConstraint("CK_blood_credit_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_blood_credit_kind", "(\"Kind\" = 'Issuance' AND \"PlayerDelta\" > 0) OR (\"Kind\" = 'Spend' AND \"PlayerDelta\" < 0) OR \"Kind\" = 'Correction'");
                    table.CheckConstraint("CK_blood_credit_reference", "length(btrim(\"ReferenceCode\")) > 0");
                    table.ForeignKey(
                        name: "FK_blood_money_credit_transactions_player_profiles_SubjectPlay~",
                        column: x => x.SubjectPlayerId,
                        principalTable: "player_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "blood_money_credit_postings",
                columns: table => new
                {
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    PostingOwner = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Amount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blood_money_credit_postings", x => new { x.TransactionId, x.LineNumber });
                    table.CheckConstraint("CK_blood_credit_posting_amount", "\"Amount\" <> 0");
                    table.CheckConstraint("CK_blood_credit_posting_owner", "(\"PostingOwner\" = 'Player' AND \"PlayerId\" IS NOT NULL AND \"LineNumber\" = 1) OR (\"PostingOwner\" = 'Treasury' AND \"PlayerId\" IS NULL AND \"LineNumber\" = 2)");
                    table.ForeignKey(
                        name: "FK_blood_money_credit_postings_blood_money_credit_transactions~",
                        column: x => x.TransactionId,
                        principalTable: "blood_money_credit_transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_blood_money_credit_postings_player_profiles_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "player_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_blood_money_credit_postings_PlayerId",
                table: "blood_money_credit_postings",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_blood_money_credit_transactions_SubjectPlayerId_CreatedAt_Id",
                table: "blood_money_credit_transactions",
                columns: new[] { "SubjectPlayerId", "CreatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blood_money_credit_accounts");

            migrationBuilder.DropTable(
                name: "blood_money_credit_postings");

            migrationBuilder.DropTable(
                name: "blood_money_credit_transactions");
        }
    }
}
