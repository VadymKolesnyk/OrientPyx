using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientPyx.DataAccess.Persistence.Migrations.Event
{
    /// <summary>
    /// The relay course mode «за етапом» (ByLeg: leg i runs course X in every team, <c>LegCoursesJson</c>) is gone. A
    /// group still in it becomes «за номером етапу» (ByBib) with the same course on every numbered team's leg — the
    /// results stay the same — and the column is dropped. Leg-number courses learn their group (<c>GroupId</c>), so a
    /// course import can replace its own group's rows and «Варіанти за номерами» can tell the group.
    /// </summary>
    public partial class DropRelayByLeg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "LegCourseAssignments",
                type: "TEXT",
                nullable: true);

            // ByLeg → ByBib: one row per numbered team and leg of the group, the leg's course from the JSON object
            // «leg → code». The active mode's courses win over rows the team already had. Ids are fresh GUID texts in
            // EF's upper-case form.
            migrationBuilder.Sql("""
                INSERT OR REPLACE INTO "LegCourseAssignments" ("Id", "EventDayId", "GroupId", "TeamNumber", "Leg", "CourseKey")
                SELECT hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-'
                           || hex(randomblob(2)) || '-' || hex(randomblob(6)),
                       s."EventDayId", s."GroupId", t."Number", CAST(j.key AS INTEGER), trim(j.value)
                FROM "GroupDaySettings" s
                JOIN "RelayTeams" t
                  ON t."EventDayId" = s."EventDayId" AND t."GroupId" = s."GroupId" AND t."Number" IS NOT NULL
                JOIN json_each(CASE WHEN json_valid(s."LegCoursesJson") THEN s."LegCoursesJson" ELSE '{}' END) j
                WHERE s."CourseAssignmentMode" = 'ByLeg'
                  AND CAST(j.key AS INTEGER) >= 1
                  AND trim(j.value) <> '';
                """);
            migrationBuilder.Sql("""
                UPDATE "GroupDaySettings" SET "CourseAssignmentMode" = 'ByBib' WHERE "CourseAssignmentMode" = 'ByLeg';
                """);

            migrationBuilder.DropColumn(
                name: "LegCoursesJson",
                table: "GroupDaySettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "LegCourseAssignments");

            migrationBuilder.AddColumn<string>(
                name: "LegCoursesJson",
                table: "GroupDaySettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }
    }
}
