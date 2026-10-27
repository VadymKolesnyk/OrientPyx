using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientPyx.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class LegCourseAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CourseSet",
                table: "RelayTeams",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CourseAssignmentMode",
                table: "GroupDaySettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "Group");

            migrationBuilder.AddColumn<string>(
                name: "LegCoursesJson",
                table: "GroupDaySettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TeamCourseSet",
                table: "GroupDaySettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "LegCourseAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventDayId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TeamNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Leg = table.Column<int>(type: "INTEGER", nullable: false),
                    CourseKey = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegCourseAssignments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegCourseAssignments_EventDayId_TeamNumber_Leg",
                table: "LegCourseAssignments",
                columns: new[] { "EventDayId", "TeamNumber", "Leg" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegCourseAssignments");

            migrationBuilder.DropColumn(
                name: "CourseSet",
                table: "RelayTeams");

            migrationBuilder.DropColumn(
                name: "CourseAssignmentMode",
                table: "GroupDaySettings");

            migrationBuilder.DropColumn(
                name: "LegCoursesJson",
                table: "GroupDaySettings");

            migrationBuilder.DropColumn(
                name: "TeamCourseSet",
                table: "GroupDaySettings");
        }
    }
}
