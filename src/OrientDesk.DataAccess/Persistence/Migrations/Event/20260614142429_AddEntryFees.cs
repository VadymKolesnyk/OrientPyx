using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientDesk.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class AddEntryFees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "EntryFee",
                table: "Groups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ChipRentalPricePerDay",
                table: "Competition",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RaisedFeeAmount",
                table: "Competition",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RaisedFeeDeadline",
                table: "Competition",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RaisedFeeEnabled",
                table: "Competition",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ChipPriceOverrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: false),
                    PricePerDay = table.Column<decimal>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChipPriceOverrides", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EntryFeeDiscounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Percent = table.Column<decimal>(type: "TEXT", nullable: false),
                    AppliesToChipRental = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntryFeeDiscounts", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChipPriceOverrides");

            migrationBuilder.DropTable(
                name: "EntryFeeDiscounts");

            migrationBuilder.DropColumn(
                name: "EntryFee",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "ChipRentalPricePerDay",
                table: "Competition");

            migrationBuilder.DropColumn(
                name: "RaisedFeeAmount",
                table: "Competition");

            migrationBuilder.DropColumn(
                name: "RaisedFeeDeadline",
                table: "Competition");

            migrationBuilder.DropColumn(
                name: "RaisedFeeEnabled",
                table: "Competition");
        }
    }
}
