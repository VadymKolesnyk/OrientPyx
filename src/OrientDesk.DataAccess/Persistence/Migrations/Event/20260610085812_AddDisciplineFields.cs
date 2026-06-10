using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientDesk.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class AddDisciplineFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PenaltyPerMinute",
                table: "GroupDaySettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequiredControlCount",
                table: "GroupDaySettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TimeLimitSeconds",
                table: "GroupDaySettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Points",
                table: "ControlPoints",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PenaltyPerMinute",
                table: "GroupDaySettings");

            migrationBuilder.DropColumn(
                name: "RequiredControlCount",
                table: "GroupDaySettings");

            migrationBuilder.DropColumn(
                name: "TimeLimitSeconds",
                table: "GroupDaySettings");

            migrationBuilder.DropColumn(
                name: "Points",
                table: "ControlPoints");
        }
    }
}
