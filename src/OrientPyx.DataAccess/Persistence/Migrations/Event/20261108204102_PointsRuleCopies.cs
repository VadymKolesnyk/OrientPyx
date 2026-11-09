using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientPyx.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class PointsRuleCopies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PointsRulesJson",
                table: "Competition",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PointsRulesJson",
                table: "Competition");
        }
    }
}
