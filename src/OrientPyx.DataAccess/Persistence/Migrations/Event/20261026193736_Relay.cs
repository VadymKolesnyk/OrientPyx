using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientPyx.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class Relay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Leg",
                table: "ParticipantDays",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RelayTeamId",
                table: "ParticipantDays",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LegCount",
                table: "GroupDaySettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "MassStartTime",
                table: "GroupDaySettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RestartRanking",
                table: "GroupDaySettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "AfterClean");

            migrationBuilder.AddColumn<string>(
                name: "RestartTimesJson",
                table: "GroupDaySettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "RelayTeams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventDayId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ClubId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RegionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OutOfCompetition = table.Column<bool>(type: "INTEGER", nullable: false),
                    StatusOverride = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelayTeams", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantDays_RelayTeamId_Leg",
                table: "ParticipantDays",
                columns: new[] { "RelayTeamId", "Leg" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RelayTeams_EventDayId_Number",
                table: "RelayTeams",
                columns: new[] { "EventDayId", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RelayTeams");

            migrationBuilder.DropIndex(
                name: "IX_ParticipantDays_RelayTeamId_Leg",
                table: "ParticipantDays");

            migrationBuilder.DropColumn(
                name: "Leg",
                table: "ParticipantDays");

            migrationBuilder.DropColumn(
                name: "RelayTeamId",
                table: "ParticipantDays");

            migrationBuilder.DropColumn(
                name: "LegCount",
                table: "GroupDaySettings");

            migrationBuilder.DropColumn(
                name: "MassStartTime",
                table: "GroupDaySettings");

            migrationBuilder.DropColumn(
                name: "RestartRanking",
                table: "GroupDaySettings");

            migrationBuilder.DropColumn(
                name: "RestartTimesJson",
                table: "GroupDaySettings");
        }
    }
}
