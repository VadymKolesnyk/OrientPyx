using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientDesk.DataAccess.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class AddOnlineApiSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OnlineIntervalSeconds",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "OnlinePublicBaseUrl",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OnlineServiceRoleKey",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OnlineSupabaseUrl",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OnlineIntervalSeconds",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "OnlinePublicBaseUrl",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "OnlineServiceRoleKey",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "OnlineSupabaseUrl",
                table: "Settings");
        }
    }
}
