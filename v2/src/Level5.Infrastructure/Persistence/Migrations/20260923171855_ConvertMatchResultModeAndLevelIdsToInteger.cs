using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Level5.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConvertMatchResultModeAndLevelIdsToInteger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Plain AlterColumn<int> here would emit "ALTER COLUMN ... TYPE integer" with no
            // USING clause, which Postgres rejects outright for character varying -> integer (no
            // implicit assignment cast exists between those types) - an explicit, reviewable
            // conversion is required rather than relying on provider-generated SQL. Every existing
            // match_results row was written by PR #33's ingestion path, which already required a
            // non-empty ModeId/LevelId string; a value that is not itself a base-10 integer here
            // indicates unexpected persisted data, and this migration deliberately fails loudly
            // (::integer raises "invalid input syntax") rather than coercing it to 0 or dropping
            // the row.
            migrationBuilder.Sql(
                """
                ALTER TABLE match_results ALTER COLUMN "ModeId" TYPE integer USING "ModeId"::integer;
                ALTER TABLE match_results ALTER COLUMN "LevelId" TYPE integer USING "LevelId"::integer;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE match_results ALTER COLUMN "ModeId" TYPE character varying(64) USING "ModeId"::character varying(64);
                ALTER TABLE match_results ALTER COLUMN "LevelId" TYPE character varying(64) USING "LevelId"::character varying(64);
                """);
        }
    }
}
