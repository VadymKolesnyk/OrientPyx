using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientDesk.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class EntryFeeDiscountFsouUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Repair databases that already accumulated more than one FSOU-member discount (from the
            // pre-index seeding race): keep the earliest-created flagged row and clear the flag on the
            // rest so they become ordinary, deletable discounts — otherwise the unique index can't apply.
            migrationBuilder.Sql(
                "UPDATE EntryFeeDiscounts SET IsFsouMemberDiscount = 0 " +
                "WHERE IsFsouMemberDiscount = 1 AND Id NOT IN (" +
                "  SELECT Id FROM EntryFeeDiscounts WHERE IsFsouMemberDiscount = 1 " +
                "  ORDER BY CreatedAt, Id LIMIT 1);");

            migrationBuilder.CreateIndex(
                name: "IX_EntryFeeDiscounts_IsFsouMemberDiscount",
                table: "EntryFeeDiscounts",
                column: "IsFsouMemberDiscount",
                unique: true,
                filter: "\"IsFsouMemberDiscount\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EntryFeeDiscounts_IsFsouMemberDiscount",
                table: "EntryFeeDiscounts");
        }
    }
}
