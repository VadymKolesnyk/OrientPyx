using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientDesk.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class MergeParticipantName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Name",
                table: "Participants");

            migrationBuilder.RenameColumn(
                name: "Surname",
                table: "Participants",
                newName: "FullName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "FullName",
                table: "Participants",
                newName: "Surname");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Participants",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }
    }
}
