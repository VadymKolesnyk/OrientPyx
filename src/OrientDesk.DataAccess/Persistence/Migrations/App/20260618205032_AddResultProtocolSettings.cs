using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientDesk.DataAccess.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class AddResultProtocolSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResultProtocolJson",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResultProtocolJson",
                table: "Settings");
        }
    }
}
