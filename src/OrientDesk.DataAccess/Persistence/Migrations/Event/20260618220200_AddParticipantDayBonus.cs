using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientDesk.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class AddParticipantDayBonus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Bonus",
                table: "ParticipantDays",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Bonus",
                table: "ParticipantDays");
        }
    }
}
