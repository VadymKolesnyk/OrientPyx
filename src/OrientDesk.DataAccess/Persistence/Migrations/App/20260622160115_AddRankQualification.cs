using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientDesk.DataAccess.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class AddRankQualification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Defaults from Додаток 89: ≥3 participants per group (п.7) and ≥8 distinct regions (п.51).
            // Matches AppSettingsRow's property defaults so an existing settings row gets the same values.
            migrationBuilder.AddColumn<int>(
                name: "RankMinParticipants",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "RankMinRegions",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 8);

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RankQualification");

            migrationBuilder.DropColumn(
                name: "RankMinParticipants",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "RankMinRegions",
                table: "Settings");
        }
    }
}
