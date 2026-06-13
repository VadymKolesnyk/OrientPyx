using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientDesk.DataAccess.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class AddSportRanks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Ranks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Points = table.Column<double>(type: "REAL", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ranks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Ranks_Name",
                table: "Ranks",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Ranks");
        }
    }
}
