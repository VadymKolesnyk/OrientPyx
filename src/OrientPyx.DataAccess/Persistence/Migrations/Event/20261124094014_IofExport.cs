using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientPyx.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class IofExport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceCourseFamily",
                table: "GroupCourseVariants",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceCourseName",
                table: "GroupCourseVariants",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IofExportSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IofExportSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IofExportSettings");

            migrationBuilder.DropColumn(
                name: "SourceCourseFamily",
                table: "GroupCourseVariants");

            migrationBuilder.DropColumn(
                name: "SourceCourseName",
                table: "GroupCourseVariants");
        }
    }
}
