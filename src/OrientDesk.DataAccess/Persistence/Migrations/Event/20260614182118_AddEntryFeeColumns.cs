using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientDesk.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class AddEntryFeeColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PaysRaisedFee",
                table: "Participants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsFsouMemberDiscount",
                table: "EntryFeeDiscounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ParticipantDiscounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DiscountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParticipantDiscounts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantDiscounts_DiscountId",
                table: "ParticipantDiscounts",
                column: "DiscountId");

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantDiscounts_ParticipantId",
                table: "ParticipantDiscounts",
                column: "ParticipantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ParticipantDiscounts");

            migrationBuilder.DropColumn(
                name: "PaysRaisedFee",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "IsFsouMemberDiscount",
                table: "EntryFeeDiscounts");
        }
    }
}
