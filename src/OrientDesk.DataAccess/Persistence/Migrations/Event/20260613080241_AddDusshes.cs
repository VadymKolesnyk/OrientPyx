using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientDesk.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class AddDusshes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DusshId",
                table: "Participants",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Dusshes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dusshes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dusshes_Name",
                table: "Dusshes",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dusshes");

            migrationBuilder.DropColumn(
                name: "DusshId",
                table: "Participants");
        }
    }
}
