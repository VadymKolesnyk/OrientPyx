using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientDesk.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class AddOfficialsToCompetitionAndGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CourseSetter",
                table: "GroupDaySettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CourseSetterCategory",
                table: "GroupDaySettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ChiefJudge",
                table: "Competition",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ChiefJudgeCategory",
                table: "Competition",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ChiefSecretary",
                table: "Competition",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ChiefSecretaryCategory",
                table: "Competition",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CourseSetter",
                table: "Competition",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CourseSetterCategory",
                table: "Competition",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Jury",
                table: "Competition",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CourseSetter",
                table: "GroupDaySettings");

            migrationBuilder.DropColumn(
                name: "CourseSetterCategory",
                table: "GroupDaySettings");

            migrationBuilder.DropColumn(
                name: "ChiefJudge",
                table: "Competition");

            migrationBuilder.DropColumn(
                name: "ChiefJudgeCategory",
                table: "Competition");

            migrationBuilder.DropColumn(
                name: "ChiefSecretary",
                table: "Competition");

            migrationBuilder.DropColumn(
                name: "ChiefSecretaryCategory",
                table: "Competition");

            migrationBuilder.DropColumn(
                name: "CourseSetter",
                table: "Competition");

            migrationBuilder.DropColumn(
                name: "CourseSetterCategory",
                table: "Competition");

            migrationBuilder.DropColumn(
                name: "Jury",
                table: "Competition");
        }
    }
}
