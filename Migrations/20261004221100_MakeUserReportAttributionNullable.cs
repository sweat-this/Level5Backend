using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Level5Backend.Migrations
{
    /// <inheritdoc />
    public partial class MakeUserReportAttributionNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "userid",
                table: "UserReport",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "userName",
                table: "UserReport",
                type: "character varying(45)",
                maxLength: 45,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(45)",
                oldMaxLength: 45);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reports accepted anonymously after Up() have no attribution. Restore the historical
            // client sentinel before reapplying NOT NULL so an emergency rollback remains viable.
            // Treat either missing half as anonymous to preserve the pair's all-or-nothing invariant.
            migrationBuilder.Sql(
                """
                UPDATE "UserReport"
                SET "userid" = 999,
                    "userName" = 'not logged in'
                WHERE "userid" IS NULL OR "userName" IS NULL;
                """);

            migrationBuilder.AlterColumn<int>(
                name: "userid",
                table: "UserReport",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "userName",
                table: "UserReport",
                type: "character varying(45)",
                maxLength: 45,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(45)",
                oldMaxLength: 45,
                oldNullable: true);
        }
    }
}
