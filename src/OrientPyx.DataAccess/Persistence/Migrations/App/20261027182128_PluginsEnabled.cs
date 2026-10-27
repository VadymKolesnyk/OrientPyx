using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientPyx.DataAccess.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class PluginsEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "DisabledPluginsJson",
                table: "Settings",
                newName: "EnabledPluginsJson");

            // The old column listed the plugins switched OFF; the new one lists those switched ON (all off by default).
            migrationBuilder.Sql("UPDATE \"Settings\" SET \"EnabledPluginsJson\" = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "EnabledPluginsJson",
                table: "Settings",
                newName: "DisabledPluginsJson");
        }
    }
}
