using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientPyx.DataAccess.Persistence.Migrations.Event
{
    /// <summary>
    /// The scatter variants table is generalised to the course variants of a group on a day (scatter orders and
    /// relay leg courses alike): same columns, new name. Renamed in place so every stored variant is kept
    /// (SQLite keeps the rows and the primary key across a rename; only the named index is recreated).
    /// </summary>
    public partial class GroupCourseVariant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ScatterVariants_EventDayId_GroupId",
                table: "ScatterVariants");

            migrationBuilder.RenameTable(
                name: "ScatterVariants",
                newName: "GroupCourseVariants");

            migrationBuilder.CreateIndex(
                name: "IX_GroupCourseVariants_EventDayId_GroupId",
                table: "GroupCourseVariants",
                columns: new[] { "EventDayId", "GroupId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GroupCourseVariants_EventDayId_GroupId",
                table: "GroupCourseVariants");

            migrationBuilder.RenameTable(
                name: "GroupCourseVariants",
                newName: "ScatterVariants");

            migrationBuilder.CreateIndex(
                name: "IX_ScatterVariants_EventDayId_GroupId",
                table: "ScatterVariants",
                columns: new[] { "EventDayId", "GroupId" });
        }
    }
}
