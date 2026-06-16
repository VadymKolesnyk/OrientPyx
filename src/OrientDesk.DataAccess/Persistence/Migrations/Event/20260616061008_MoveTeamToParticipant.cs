using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientDesk.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class MoveTeamToParticipant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Team",
                table: "ParticipantDays");

            migrationBuilder.AddColumn<string>(
                name: "Team",
                table: "Participants",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Team",
                table: "Participants");

            migrationBuilder.AddColumn<string>(
                name: "Team",
                table: "ParticipantDays",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }
    }
}
