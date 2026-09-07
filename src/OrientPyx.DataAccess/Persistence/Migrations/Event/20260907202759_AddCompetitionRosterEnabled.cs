using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientPyx.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class AddCompetitionRosterEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RosterEnabled",
                table: "Competition",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RosterEnabled",
                table: "Competition");
        }
    }
}
