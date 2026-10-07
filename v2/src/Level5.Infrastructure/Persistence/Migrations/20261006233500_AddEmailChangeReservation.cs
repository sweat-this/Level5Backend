using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Level5.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailChangeReservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Before this migration the only supported writer was initial email verification,
            // whose challenge target always matched the owning account's EmailCanonical. The
            // existing unique accounts.EmailCanonical index therefore made duplicate active
            // targets impossible in durable application-created data. Keep CreateIndex strict:
            // unexpected out-of-band duplicates must fail the upgrade rather than silently
            // choosing which account retains the reservation.
            migrationBuilder.CreateIndex(
                name: "IX_email_verification_challenges_TargetEmailCanonical",
                table: "email_verification_challenges",
                column: "TargetEmailCanonical",
                unique: true,
                filter: "\"ConsumedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_email_verification_challenges_TargetEmailCanonical",
                table: "email_verification_challenges");
        }
    }
}
