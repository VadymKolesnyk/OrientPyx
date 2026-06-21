using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientDesk.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class AddResultProtocolSettingsPerDay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResultProtocolSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventDayId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResultProtocolSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResultProtocolSettings_EventDayId",
                table: "ResultProtocolSettings",
                column: "EventDayId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResultProtocolSettings");
        }
    }
}
