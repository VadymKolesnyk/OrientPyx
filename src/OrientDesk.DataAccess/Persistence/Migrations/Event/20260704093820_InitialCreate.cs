using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrientDesk.DataAccess.Persistence.Migrations.Event
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                name: "Clubs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clubs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Competition",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Identifier = table.Column<string>(type: "TEXT", nullable: false),
                    Venue = table.Column<string>(type: "TEXT", nullable: false),
                    Organisation = table.Column<string>(type: "TEXT", nullable: false),
                    StartDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    EndDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RaisedFeeEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RaisedFeeAmount = table.Column<decimal>(type: "TEXT", nullable: true),
                    ChipRentalPricePerDay = table.Column<decimal>(type: "TEXT", nullable: true),
                    CourseSetter = table.Column<string>(type: "TEXT", nullable: false),
                    CourseSetterCategory = table.Column<string>(type: "TEXT", nullable: false),
                    ChiefJudge = table.Column<string>(type: "TEXT", nullable: false),
                    ChiefJudgeCategory = table.Column<string>(type: "TEXT", nullable: false),
                    ChiefSecretary = table.Column<string>(type: "TEXT", nullable: false),
                    ChiefSecretaryCategory = table.Column<string>(type: "TEXT", nullable: false),
                    Jury = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultPointsRuleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Competition", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ControlPoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventDayId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Latitude = table.Column<double>(type: "REAL", nullable: true),
                    Longitude = table.Column<double>(type: "REAL", nullable: true),
                    MapX = table.Column<double>(type: "REAL", nullable: true),
                    MapY = table.Column<double>(type: "REAL", nullable: true),
                    MapScale = table.Column<int>(type: "INTEGER", nullable: true),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Points = table.Column<int>(type: "INTEGER", nullable: true),
                    IsDisabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ControlPoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Days",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Venue = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultDiscipline = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Days", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dusshes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dusshes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EntryFeeDiscounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Percent = table.Column<decimal>(type: "TEXT", nullable: false),
                    AppliesToChipRental = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsFsouMemberDiscount = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntryFeeDiscounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FinishReadouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventDayId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    ChipNumber = table.Column<string>(type: "TEXT", nullable: false),
                    StartTime = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FinishTime = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Punches = table.Column<string>(type: "TEXT", nullable: false),
                    PunchTimes = table.Column<string>(type: "TEXT", nullable: false),
                    ContentKey = table.Column<string>(type: "TEXT", nullable: false),
                    ManualStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinishReadouts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GroupDaySettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventDayId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    CourseOrder = table.Column<string>(type: "TEXT", nullable: false),
                    DistanceKm = table.Column<decimal>(type: "TEXT", nullable: true),
                    DisciplineOverride = table.Column<string>(type: "TEXT", nullable: true),
                    TimeLimitSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    CourseSetter = table.Column<string>(type: "TEXT", nullable: false),
                    CourseSetterCategory = table.Column<string>(type: "TEXT", nullable: false),
                    RequiredControlCount = table.Column<int>(type: "INTEGER", nullable: true),
                    PenaltyPerMinute = table.Column<decimal>(type: "TEXT", nullable: true),
                    PointsRuleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RankLevel = table.Column<string>(type: "TEXT", nullable: false),
                    MasterCount = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupDaySettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    EntryFee = table.Column<decimal>(type: "TEXT", nullable: true),
                    MinBirthYear = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxBirthYear = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MonitorSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitorSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OnlinePublishSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnlinePublishSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ParticipantDays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventDayId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Chip = table.Column<string>(type: "TEXT", nullable: false),
                    StartTime = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    OutOfCompetition = table.Column<bool>(type: "INTEGER", nullable: false),
                    Bonus = table.Column<int>(type: "INTEGER", nullable: true),
                    ResultStatusOverride = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParticipantDays", x => x.Id);
                });

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

            migrationBuilder.CreateTable(
                name: "Participants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FullName = table.Column<string>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", nullable: false),
                    Rank = table.Column<string>(type: "TEXT", nullable: false),
                    Coach = table.Column<string>(type: "TEXT", nullable: false),
                    BirthDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RegionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClubId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DusshId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Representative = table.Column<string>(type: "TEXT", nullable: false),
                    FsouCode = table.Column<string>(type: "TEXT", nullable: false),
                    IsFsouMember = table.Column<bool>(type: "INTEGER", nullable: false),
                    Payment = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: false),
                    Team = table.Column<string>(type: "TEXT", nullable: false),
                    PaysRaisedFee = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Participants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Regions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Regions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RentalChips",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RentalChips", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ResultProtocolSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventDayId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResultProtocolSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StartProtocolSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventDayId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StartProtocolSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SummaryProtocolSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SummaryProtocolSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Clubs_Name",
                table: "Clubs",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dusshes_Name",
                table: "Dusshes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EntryFeeDiscounts_IsFsouMemberDiscount",
                table: "EntryFeeDiscounts",
                column: "IsFsouMemberDiscount",
                unique: true,
                filter: "\"IsFsouMemberDiscount\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_FinishReadouts_EventDayId",
                table: "FinishReadouts",
                column: "EventDayId");

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantDays_EventDayId",
                table: "ParticipantDays",
                column: "EventDayId");

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantDays_ParticipantId",
                table: "ParticipantDays",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantDiscounts_DiscountId",
                table: "ParticipantDiscounts",
                column: "DiscountId");

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantDiscounts_ParticipantId",
                table: "ParticipantDiscounts",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_Regions_Name",
                table: "Regions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RentalChips_Number",
                table: "RentalChips",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResultProtocolSettings_EventDayId",
                table: "ResultProtocolSettings",
                column: "EventDayId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StartProtocolSettings_EventDayId_Kind",
                table: "StartProtocolSettings",
                columns: new[] { "EventDayId", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChipPriceOverrides");

            migrationBuilder.DropTable(
                name: "Clubs");

            migrationBuilder.DropTable(
                name: "Competition");

            migrationBuilder.DropTable(
                name: "ControlPoints");

            migrationBuilder.DropTable(
                name: "Days");

            migrationBuilder.DropTable(
                name: "Dusshes");

            migrationBuilder.DropTable(
                name: "EntryFeeDiscounts");

            migrationBuilder.DropTable(
                name: "FinishReadouts");

            migrationBuilder.DropTable(
                name: "GroupDaySettings");

            migrationBuilder.DropTable(
                name: "Groups");

            migrationBuilder.DropTable(
                name: "MonitorSettings");

            migrationBuilder.DropTable(
                name: "OnlinePublishSettings");

            migrationBuilder.DropTable(
                name: "ParticipantDays");

            migrationBuilder.DropTable(
                name: "ParticipantDiscounts");

            migrationBuilder.DropTable(
                name: "Participants");

            migrationBuilder.DropTable(
                name: "Regions");

            migrationBuilder.DropTable(
                name: "RentalChips");

            migrationBuilder.DropTable(
                name: "ResultProtocolSettings");

            migrationBuilder.DropTable(
                name: "StartProtocolSettings");

            migrationBuilder.DropTable(
                name: "SummaryProtocolSettings");
        }
    }
}
