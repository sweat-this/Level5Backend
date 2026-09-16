using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Level5.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenFriendshipInvariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LowerPlayerId",
                table: "friend_requests",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "friend_requests",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "UpperPlayerId",
                table: "friend_requests",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Backfill any pre-existing rows' canonical pair from their real FromPlayerId/ToPlayerId
            // rather than leaving the all-zero default above - required so the unique partial index
            // created below doesn't spuriously collide two unrelated pairs that both defaulted to
            // the same all-zero value.
            //
            // CAVEAT: LEAST/GREATEST order by Postgres's native uuid byte ordering, which is not
            // guaranteed to agree with FriendshipStore.AddRequestAsync's canonical order
            // (Friendship.Order, i.e. .NET's Guid.CompareTo - a different field layout). A
            // backfilled historical row could therefore land on a different (Lower, Upper) pair
            // than a new row the application inserts for the same two players, letting a duplicate
            // slip past the new unique index for that specific pair. This is safe only because no
            // V2 environment has ever run with real data (see the "Migration history" section of
            // v2/README.md) - there is nothing to backfill today. If this migration is ever applied
            // to a database that already holds Pending friend_requests rows, recompute this backfill
            // to match Guid.CompareTo's ordering (or re-derive it in application code) instead of
            // relying on this SQL as-is.
            migrationBuilder.Sql(
                """
                UPDATE friend_requests
                SET "LowerPlayerId" = LEAST("FromPlayerId", "ToPlayerId"),
                    "UpperPlayerId" = GREATEST("FromPlayerId", "ToPlayerId");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_friend_requests_LowerPlayerId_UpperPlayerId",
                table: "friend_requests",
                columns: new[] { "LowerPlayerId", "UpperPlayerId" },
                unique: true,
                filter: "\"Status\" = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_friend_requests_LowerPlayerId_UpperPlayerId",
                table: "friend_requests");

            migrationBuilder.DropColumn(
                name: "LowerPlayerId",
                table: "friend_requests");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "friend_requests");

            migrationBuilder.DropColumn(
                name: "UpperPlayerId",
                table: "friend_requests");
        }
    }
}
