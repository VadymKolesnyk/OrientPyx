using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientPyx.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class AddEventDayIsLocked : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsLocked",
                table: "Days",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsLocked",
                table: "Days");
        }
    }
}
