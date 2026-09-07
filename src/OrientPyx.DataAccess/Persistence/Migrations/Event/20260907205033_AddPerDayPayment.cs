using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientPyx.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class AddPerDayPayment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Payment",
                table: "ParticipantDays",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "PaymentPerDay",
                table: "Competition",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Payment",
                table: "ParticipantDays");

            migrationBuilder.DropColumn(
                name: "PaymentPerDay",
                table: "Competition");
        }
    }
}
