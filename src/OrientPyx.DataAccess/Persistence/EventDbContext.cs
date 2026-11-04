using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;

namespace OrientPyx.DataAccess.Persistence;

/// <summary>
/// EF Core context for a single competition's database (./events/&lt;id&gt;/event.db).
/// Opened dynamically per path via <see cref="EventDbContextFactory"/>.
/// </summary>
public class EventDbContext : DbContext
{
    public EventDbContext(DbContextOptions<EventDbContext> options)
        : base(options)
    {
    }

    public DbSet<CompetitionInfo> Competition => Set<CompetitionInfo>();
    public DbSet<EventDay> Days => Set<EventDay>();
    public DbSet<ControlPoint> ControlPoints => Set<ControlPoint>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupDaySettings> GroupDaySettings => Set<GroupDaySettings>();
    public DbSet<GroupCourseVariant> GroupCourseVariants => Set<GroupCourseVariant>();
    public DbSet<RentalChip> RentalChips => Set<RentalChip>();
    public DbSet<Region> Regions => Set<Region>();
    public DbSet<Club> Clubs => Set<Club>();
    public DbSet<Dussh> Dusshes => Set<Dussh>();
    public DbSet<Participant> Participants => Set<Participant>();
    public DbSet<ParticipantDay> ParticipantDays => Set<ParticipantDay>();
    public DbSet<ParticipantDiscount> ParticipantDiscounts => Set<ParticipantDiscount>();
    public DbSet<FinishReadout> FinishReadouts => Set<FinishReadout>();
    public DbSet<ChipPriceOverride> ChipPriceOverrides => Set<ChipPriceOverride>();
    public DbSet<EntryFeeDiscount> EntryFeeDiscounts => Set<EntryFeeDiscount>();
    public DbSet<ResultProtocolSettingsRow> ResultProtocolSettings => Set<ResultProtocolSettingsRow>();
    public DbSet<StartProtocolSettingsRow> StartProtocolSettings => Set<StartProtocolSettingsRow>();
    public DbSet<SummaryProtocolSettingsRow> SummaryProtocolSettings => Set<SummaryProtocolSettingsRow>();
    public DbSet<StatementSettingsRow> StatementSettings => Set<StatementSettingsRow>();
    public DbSet<ChipScanSettingsRow> ChipScanSettings => Set<ChipScanSettingsRow>();
    public DbSet<OnlinePublishSettingsRow> OnlinePublishSettings => Set<OnlinePublishSettingsRow>();
    public DbSet<MonitorSettingsRow> MonitorSettings => Set<MonitorSettingsRow>();
    public DbSet<DrawSettingsRow> DrawSettings => Set<DrawSettingsRow>();
    public DbSet<PluginSettingsRow> PluginSettings => Set<PluginSettingsRow>();
    public DbSet<CustomFieldDef> CustomFields => Set<CustomFieldDef>();
    public DbSet<ParticipantFieldValue> ParticipantFieldValues => Set<ParticipantFieldValue>();
    public DbSet<ImportMappingRow> ImportMappings => Set<ImportMappingRow>();
    public DbSet<RelayTeam> RelayTeams => Set<RelayTeam>();
    public DbSet<LegCourseAssignment> LegCourseAssignments => Set<LegCourseAssignment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Store enums by their string name (string-enum at the DB level), not as integers.
        modelBuilder.Entity<ControlPoint>()
            .Property(cp => cp.Type)
            .HasConversion<string>();

        // A discipline id persists as its string (the built-in ids equal the former enum names).
        var disciplineId = new ValueConverter<DisciplineId, string>(id => id.Value, s => new DisciplineId(s));
        modelBuilder.Entity<EventDay>()
            .Property(d => d.DefaultDiscipline)
            .HasConversion(disciplineId);

        // Nullable enum: null persists as NULL (= inherit the day's default discipline).
        modelBuilder.Entity<GroupDaySettings>()
            .Property(g => g.DisciplineOverride)
            .HasConversion(disciplineId);

        // The group's rank level (Додаток 89) persists as its string name.
        modelBuilder.Entity<GroupDaySettings>()
            .Property(g => g.RankLevel)
            .HasConversion<string>();

        // A chip number identifies one rental chip per competition; the unique index both enforces
        // that and speeds the future join against participant entries by chip number.
        modelBuilder.Entity<RentalChip>()
            .HasIndex(c => c.Number)
            .IsUnique();

        // A region name identifies one region per competition; the unique index enforces that (the
        // service layer additionally guards case-insensitive collisions).
        modelBuilder.Entity<Region>()
            .HasIndex(r => r.Name)
            .IsUnique();

        // A club name is likewise unique per competition.
        modelBuilder.Entity<Club>()
            .HasIndex(c => c.Name)
            .IsUnique();

        // A sports-school (ДЮСШ) name is likewise unique per competition.
        modelBuilder.Entity<Dussh>()
            .HasIndex(d => d.Name)
            .IsUnique();

        // Per-day links are queried by day (the day's grid) and by participant (the cascade-delete
        // and roster aggregation). Number/chip uniqueness is enforced in the service layer rather
        // than by a DB index, because blank values are allowed and must not collide with each other.
        modelBuilder.Entity<ParticipantDay>()
            .HasIndex(p => p.EventDayId);

        modelBuilder.Entity<ParticipantDay>()
            .HasIndex(p => p.ParticipantId);

        // A participant runs a day at most once, so (day, participant) is unique. Enforced by the database
        // because a duplicate link is silently destructive — it prints the runner twice on every protocol —
        // and the service-layer "does a link already exist?" check is a read-then-write that two concurrent
        // callers can both pass. Also covers the by-participant lookups the previous index served.
        modelBuilder.Entity<ParticipantDay>()
            .HasIndex(p => new { p.EventDayId, p.ParticipantId })
            .IsUnique();

        // Finish read-outs are queried by day (the day's log) and de-duplicated by content within a day.
        modelBuilder.Entity<FinishReadout>()
            .HasIndex(r => r.EventDayId);

        // Participant↔discount links are queried by participant (a row's selected discounts) and by
        // discount (clearing a discount from everyone when it is deleted).
        modelBuilder.Entity<ParticipantDiscount>()
            .HasIndex(p => p.ParticipantId);

        modelBuilder.Entity<ParticipantDiscount>()
            .HasIndex(p => p.DiscountId);

        // Exactly one discount carries the FSOU-member flag (it is seeded once per competition). A
        // filtered unique index makes a second flagged row impossible at the DB level, guarding against
        // a race where two concurrent loads both seed it (see EventStore.GetEntryFeeDiscountsAsync).
        modelBuilder.Entity<EntryFeeDiscount>()
            .HasIndex(d => d.IsFsouMemberDiscount)
            .IsUnique()
            .HasFilter("\"IsFsouMemberDiscount\" = 1");

        // At most one protocol template per day; the unique index enforces it and speeds the by-day lookup.
        modelBuilder.Entity<ResultProtocolSettingsRow>()
            .HasIndex(r => r.EventDayId)
            .IsUnique();

        // Start-protocol templates: the kind persists as its string name; at most one row per (day, kind).
        modelBuilder.Entity<StartProtocolSettingsRow>()
            .Property(r => r.Kind)
            .HasConversion<string>();

        modelBuilder.Entity<StartProtocolSettingsRow>()
            .HasIndex(r => new { r.EventDayId, r.Kind })
            .IsUnique();

        // Start-draw settings: the kind persists as its string name; at most one row per (day, kind).
        modelBuilder.Entity<DrawSettingsRow>()
            .Property(r => r.Kind)
            .HasConversion<string>();

        modelBuilder.Entity<DrawSettingsRow>()
            .HasIndex(r => new { r.EventDayId, r.Kind })
            .IsUnique();

        // Scatter («розсіювання») variants: several per (day, group). Non-unique index speeds the per-day
        // load and the per-group replace; Order sorts them (A before B).
        modelBuilder.Entity<GroupCourseVariant>()
            .HasIndex(v => new { v.EventDayId, v.GroupId });

        // Discipline settings (Р-11): one row per (day, discipline); the competition-scoped row has no day.
        modelBuilder.Entity<PluginSettingsRow>()
            .Property(r => r.DisciplineId)
            .HasConversion(disciplineId);

        modelBuilder.Entity<PluginSettingsRow>()
            .HasIndex(r => new { r.EventDayId, r.DisciplineId })
            .IsUnique();

        // Additional participant fields (Р-13): keyed by their stable key; one value per (participant, field).
        // ParticipantId is a foreign key by convention only (like ParticipantDay), so the store deletes the values
        // together with the participant / the field.
        modelBuilder.Entity<CustomFieldDef>().HasKey(f => f.Key);
        modelBuilder.Entity<ParticipantFieldValue>().HasKey(v => new { v.ParticipantId, v.Key });
        modelBuilder.Entity<ParticipantFieldValue>().HasIndex(v => v.Key);

        // Relay (Р-20, Р-21): a team number is unique within the day; a leg of a team holds at most one runner.
        modelBuilder.Entity<RelayTeam>()
            .HasIndex(t => new { t.EventDayId, t.Number })
            .IsUnique();

        modelBuilder.Entity<RelayTeam>()
            .Property(t => t.StatusOverride)
            .HasConversion<string>();

        modelBuilder.Entity<ParticipantDay>()
            .HasIndex(p => new { p.RelayTeamId, p.Leg })
            .IsUnique();

        modelBuilder.Entity<GroupDaySettings>()
            .Property(g => g.RestartRanking)
            .HasConversion<string>();

        // Relay leg courses (Р-41): the mode is stored by name; one course per leg number of the day.
        modelBuilder.Entity<GroupDaySettings>()
            .Property(g => g.CourseAssignmentMode)
            .HasConversion<string>();

        modelBuilder.Entity<LegCourseAssignment>()
            .HasIndex(a => new { a.EventDayId, a.TeamNumber, a.Leg })
            .IsUnique();

        // The last CSV/Excel column mapping per header set.
        modelBuilder.Entity<ImportMappingRow>()
            .HasIndex(r => r.HeaderSetHash)
            .IsUnique();
    }
}
