using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientDesk.DataAccess.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class AddRankCountForRank : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default 12 so an existing settings row upgrades to the documented default rather than 0
            // (which would sum no participants and force every group's course rank to zero).
            migrationBuilder.AddColumn<int>(
                name: "RankCountForRank",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 12);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RankCountForRank",
                table: "Settings");
        }
    }
}
