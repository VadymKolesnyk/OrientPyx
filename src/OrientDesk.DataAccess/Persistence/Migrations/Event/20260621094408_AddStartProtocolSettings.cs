using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientDesk.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class AddStartProtocolSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StartProtocolSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventDayId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StartProtocolSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StartProtocolSettings_EventDayId_Kind",
                table: "StartProtocolSettings",
                columns: new[] { "EventDayId", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StartProtocolSettings");
        }
    }
}
