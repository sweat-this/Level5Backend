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
