using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientPyx.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class UniqueParticipantDayPerDay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing databases may already hold duplicate links for the same (day, participant) — the
            // unique index below cannot be created while they are there. A participant runs a day once, so
            // a repeat is always bad data: keep the row that carries the most information (a start time,
            // then a chip, then a group) and delete the rest, preferring the earliest Order on a tie so the
            // day-grid position is preserved.
            migrationBuilder.Sql(@"
                DELETE FROM ParticipantDays
                WHERE Id NOT IN (
                    SELECT Id FROM (
                        SELECT Id, ROW_NUMBER() OVER (
                            PARTITION BY EventDayId, ParticipantId
                            ORDER BY
                                CASE WHEN StartTime IS NOT NULL AND StartTime <> '' THEN 0 ELSE 1 END,
                                CASE WHEN Chip IS NOT NULL AND Chip <> '' THEN 0 ELSE 1 END,
                                CASE WHEN GroupId IS NOT NULL THEN 0 ELSE 1 END,
                                ""Order""
                        ) AS rn
                        FROM ParticipantDays
                    )
                    WHERE rn = 1
                );");

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantDays_EventDayId_ParticipantId",
                table: "ParticipantDays",
                columns: new[] { "EventDayId", "ParticipantId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ParticipantDays_EventDayId_ParticipantId",
                table: "ParticipantDays");
        }
    }
}
