using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientPyx.DataAccess.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LastSession",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    LastEventIdentifier = table.Column<string>(type: "TEXT", nullable: true),
                    LastEventDayNumber = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LastSession", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PointsRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    TableJson = table.Column<string>(type: "TEXT", nullable: true),
                    Formula = table.Column<string>(type: "TEXT", nullable: true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PointsRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RankQualification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeKms = table.Column<int>(type: "INTEGER", nullable: true),
                    TimeFirst = table.Column<int>(type: "INTEGER", nullable: true),
                    TimeSecond = table.Column<int>(type: "INTEGER", nullable: true),
                    TimeThird = table.Column<int>(type: "INTEGER", nullable: true),
                    TimeThirdJunior = table.Column<int>(type: "INTEGER", nullable: true),
                    PointsKms = table.Column<int>(type: "INTEGER", nullable: true),
                    PointsFirst = table.Column<int>(type: "INTEGER", nullable: true),
                    PointsSecond = table.Column<int>(type: "INTEGER", nullable: true),
                    PointsThird = table.Column<int>(type: "INTEGER", nullable: true),
                    PointsThirdJunior = table.Column<int>(type: "INTEGER", nullable: true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RankQualification", x => x.Id);
                });

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

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    EventsPath = table.Column<string>(type: "TEXT", nullable: false),
                    FontScale = table.Column<double>(type: "REAL", nullable: false),
                    PrinterName = table.Column<string>(type: "TEXT", nullable: false),
                    ReceiptWidthMm = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultProtocolJson = table.Column<string>(type: "TEXT", nullable: false),
                    StartProtocolRegularJson = table.Column<string>(type: "TEXT", nullable: false),
                    StartProtocolJudgesJson = table.Column<string>(type: "TEXT", nullable: false),
                    RankMinParticipants = table.Column<int>(type: "INTEGER", nullable: false),
                    RankMinRegions = table.Column<int>(type: "INTEGER", nullable: false),
                    RankCountForRank = table.Column<int>(type: "INTEGER", nullable: false),
                    OnlineSupabaseUrl = table.Column<string>(type: "TEXT", nullable: false),
                    OnlineServiceRoleKey = table.Column<string>(type: "TEXT", nullable: false),
                    OnlinePublicBaseUrl = table.Column<string>(type: "TEXT", nullable: false),
                    OnlineIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ReadoutType = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PointsRules_Name",
                table: "PointsRules",
                column: "Name",
                unique: true,
                filter: "\"Name\" <> ''");

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
            migrationBuilder.DropTable(
                name: "LastSession");

            migrationBuilder.DropTable(
                name: "PointsRules");

            migrationBuilder.DropTable(
                name: "RankQualification");

            migrationBuilder.DropTable(
                name: "Ranks");

            migrationBuilder.DropTable(
                name: "Settings");
        }
    }
}
