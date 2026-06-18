using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientDesk.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class AddControlPointMapPosition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MapScale",
                table: "ControlPoints",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MapX",
                table: "ControlPoints",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MapY",
                table: "ControlPoints",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MapScale",
                table: "ControlPoints");

            migrationBuilder.DropColumn(
                name: "MapX",
                table: "ControlPoints");

            migrationBuilder.DropColumn(
                name: "MapY",
                table: "ControlPoints");
        }
    }
}
