using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientDesk.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class RenameDistanceToKm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DistanceMeters",
                table: "GroupDaySettings");

            migrationBuilder.AddColumn<decimal>(
                name: "DistanceKm",
                table: "GroupDaySettings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DistanceKm",
                table: "GroupDaySettings");

            migrationBuilder.AddColumn<int>(
                name: "DistanceMeters",
                table: "GroupDaySettings",
                type: "INTEGER",
                nullable: true);
        }
    }
}
