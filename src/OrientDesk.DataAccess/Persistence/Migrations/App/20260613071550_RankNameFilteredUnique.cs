using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientDesk.DataAccess.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class RankNameFilteredUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Ranks_Name",
                table: "Ranks");

            migrationBuilder.CreateIndex(
                name: "IX_Ranks_Name",
                table: "Ranks",
                column: "Name",
                unique: true,
                filter: "\"Name\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Ranks_Name",
                table: "Ranks");

            migrationBuilder.CreateIndex(
                name: "IX_Ranks_Name",
                table: "Ranks",
                column: "Name",
                unique: true);
        }
    }
}
