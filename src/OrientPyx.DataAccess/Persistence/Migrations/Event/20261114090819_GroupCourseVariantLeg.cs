using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientPyx.DataAccess.Persistence.Migrations.Event
{
    /// <summary>
    /// A relay «своя на кожен етап» course names its leg in a column of its own (<c>Leg</c>), so a leg can have several
    /// courses with any codes. Before, leg N ran the course coded «N»: such a course of a group in that mode gets leg N.
    /// </summary>
    public partial class GroupCourseVariantLeg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Leg",
                table: "GroupCourseVariants",
                type: "INTEGER",
                nullable: true);

            // A code that is a plain positive number («2», never «02» or «2A») of a group in «PerLeg» on that day.
            migrationBuilder.Sql("""
                UPDATE "GroupCourseVariants"
                SET "Leg" = CAST(trim("Code") AS INTEGER)
                WHERE trim("Code") GLOB '[1-9]*'
                  AND trim("Code") NOT GLOB '*[^0-9]*'
                  AND EXISTS (
                      SELECT 1 FROM "GroupDaySettings" s
                      WHERE s."EventDayId" = "GroupCourseVariants"."EventDayId"
                        AND s."GroupId" = "GroupCourseVariants"."GroupId"
                        AND s."CourseAssignmentMode" = 'PerLeg');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Leg",
                table: "GroupCourseVariants");
        }
    }
}
