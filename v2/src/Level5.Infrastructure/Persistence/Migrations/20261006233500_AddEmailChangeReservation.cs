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
            migrationBuilder.CreateIndex(
                name: "IX_email_verification_challenges_TargetEmailCanonical",
                table: "email_verification_challenges",
                column: "TargetEmailCanonical",
                unique: true);
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
