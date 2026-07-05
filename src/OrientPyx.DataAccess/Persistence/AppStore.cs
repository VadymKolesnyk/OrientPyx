using Microsoft.EntityFrameworkCore;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.DataAccess.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IAppStore"/> over the shared app database.
/// Stateless — creates a short-lived context per operation via the factory — so it can be
/// consumed by singleton services (e.g. the session) without a captive-dependency problem.
/// </summary>
public sealed class AppStore : IAppStore
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public AppStore(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public AppPaths GetDefaultPaths() => new()
    {
        EventsPath = AppDatabasePaths.DefaultEventsPath
    };

    public async Task<AppPaths?> GetPathsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (row is null)
            return null;

        // A path stored inside the install directory (an earlier build baked in the absolute
        // ...\current\events) is a data-loss trap: auto-updates wipe that folder. Drop it so the caller
        // falls back to the default, which resolves to the update-safe data-root.
        var eventsPath = AppDatabasePaths.IsInsideApplicationDirectory(row.EventsPath)
            ? string.Empty
            : row.EventsPath;

        return new AppPaths { EventsPath = eventsPath };
    }

    public async Task SavePathsAsync(AppPaths paths, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            db.Settings.Add(new AppSettingsRow { Id = 1, EventsPath = paths.EventsPath });
        }
        else
        {
            row.EventsPath = paths.EventsPath;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<double?> GetFontScaleAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return row?.FontScale;
    }

    public async Task SaveFontScaleAsync(double fontScale, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            db.Settings.Add(new AppSettingsRow { Id = 1, FontScale = fontScale });
        }
        else
        {
            row.FontScale = fontScale;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<(string PrinterName, int WidthMm)?> GetPrintSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (row is null)
            return null;

        return (row.PrinterName, row.ReceiptWidthMm);
    }

    public async Task SavePrintSettingsAsync(string printerName, int widthMm, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            db.Settings.Add(new AppSettingsRow { Id = 1, PrinterName = printerName, ReceiptWidthMm = widthMm });
        }
        else
        {
            row.PrinterName = printerName;
            row.ReceiptWidthMm = widthMm;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int?> GetReadoutTypeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return row?.ReadoutType;
    }

    public async Task SaveReadoutTypeAsync(int readoutType, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
            db.Settings.Add(new AppSettingsRow { Id = 1, ReadoutType = readoutType });
        else
            row.ReadoutType = readoutType;

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> GetLanguageAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return row?.Language;
    }

    public async Task SaveLanguageAsync(string language, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
            db.Settings.Add(new AppSettingsRow { Id = 1, Language = language });
        else
            row.Language = language;

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> GetResultProtocolJsonAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return row?.ResultProtocolJson;
    }

    public async Task SaveResultProtocolJsonAsync(string json, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            db.Settings.Add(new AppSettingsRow { Id = 1, ResultProtocolJson = json });
        }
        else
        {
            row.ResultProtocolJson = json;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> GetStartProtocolJsonAsync(StartProtocolKind kind, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (row is null)
            return null;
        return kind == StartProtocolKind.Judges ? row.StartProtocolJudgesJson : row.StartProtocolRegularJson;
    }

    public async Task SaveStartProtocolJsonAsync(StartProtocolKind kind, string json, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new AppSettingsRow { Id = 1 };
            db.Settings.Add(row);
        }
        if (kind == StartProtocolKind.Judges)
            row.StartProtocolJudgesJson = json;
        else
            row.StartProtocolRegularJson = json;

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<(int MinParticipants, int MinRegions, int CountForRank)?> GetRankConditionsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (row is null)
            return null;
        return (row.RankMinParticipants, row.RankMinRegions, row.RankCountForRank);
    }

    public async Task SaveRankConditionsAsync(int minParticipants, int minRegions, int countForRank, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            db.Settings.Add(new AppSettingsRow
            {
                Id = 1,
                RankMinParticipants = minParticipants,
                RankMinRegions = minRegions,
                RankCountForRank = countForRank
            });
        }
        else
        {
            row.RankMinParticipants = minParticipants;
            row.RankMinRegions = minRegions;
            row.RankCountForRank = countForRank;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<OnlineApiSettings> GetOnlineApiSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (row is null)
            return OnlineApiSettings.Empty;

        return new OnlineApiSettings(
            row.OnlineSupabaseUrl,
            row.OnlineServiceRoleKey,
            row.OnlinePublicBaseUrl,
            row.OnlineIntervalSeconds <= 0 ? OnlineApiSettings.DefaultIntervalSeconds : row.OnlineIntervalSeconds);
    }

    public async Task SaveOnlineApiSettingsAsync(OnlineApiSettings settings, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new AppSettingsRow { Id = 1 };
            db.Settings.Add(row);
        }

        row.OnlineSupabaseUrl = settings.SupabaseUrl;
        row.OnlineServiceRoleKey = settings.ServiceRoleKey;
        row.OnlinePublicBaseUrl = settings.PublicBaseUrl;
        row.OnlineIntervalSeconds = settings.IntervalSeconds;

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<(string? Identifier, int? DayNumber)> GetLastSessionAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.LastSession.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return (row?.LastEventIdentifier, row?.LastEventDayNumber);
    }

    public async Task SaveLastSessionAsync(string? identifier, int? dayNumber, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.LastSession.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            db.LastSession.Add(new LastSessionRow { Id = 1, LastEventIdentifier = identifier, LastEventDayNumber = dayNumber });
        }
        else
        {
            row.LastEventIdentifier = identifier;
            row.LastEventDayNumber = dayNumber;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    // ── Sports ranks ───────────────────────────────────────────────────────────────────────────────

    public async Task SeedRanksIfEmptyAsync(IReadOnlyList<SportRank> ranks, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Ranks.AnyAsync(cancellationToken))
            return;

        db.Ranks.AddRange(ranks);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SportRank>> GetRanksAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Ranks
            .AsNoTracking()
            .OrderBy(r => r.Order)
            .ThenBy(r => r.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<SportRank> AddRankAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var maxOrder = await db.Ranks.AnyAsync(cancellationToken)
            ? await db.Ranks.MaxAsync(r => r.Order, cancellationToken)
            : -1;

        var rank = new SportRank { Name = string.Empty, Points = 0, Order = maxOrder + 1 };
        db.Ranks.Add(rank);
        await db.SaveChangesAsync(cancellationToken);
        return rank;
    }

    public async Task UpdateRankAsync(SportRank rank, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.Ranks.FirstOrDefaultAsync(r => r.Id == rank.Id, cancellationToken);
        if (existing is null)
            return;

        // Reject a rename that would collide with another rank (case-insensitive); keep the old name.
        var name = (rank.Name ?? string.Empty).Trim();
        if (name.Length > 0)
        {
            var clash = await db.Ranks.AnyAsync(
                r => r.Id != rank.Id && r.Name == name, cancellationToken);
            if (!clash)
                existing.Name = name;
        }
        else
        {
            existing.Name = string.Empty;
        }

        existing.Points = rank.Points;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRankAsync(Guid rankId, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Ranks.FirstOrDefaultAsync(r => r.Id == rankId, cancellationToken);
        if (existing is null)
            return;

        db.Ranks.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }

    // ── Points rules ─────────────────────────────────────────────────────────────────────────────────

    public async Task SeedPointsRulesIfEmptyAsync(IReadOnlyList<PointsRule> rules, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.PointsRules.AnyAsync(cancellationToken))
            return;

        db.PointsRules.AddRange(rules);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PointsRule>> GetPointsRulesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.PointsRules
            .AsNoTracking()
            .OrderBy(r => r.Order)
            .ThenBy(r => r.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<PointsRule> AddPointsRuleAsync(PointsRuleKind kind, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var maxOrder = await db.PointsRules.AnyAsync(cancellationToken)
            ? await db.PointsRules.MaxAsync(r => r.Order, cancellationToken)
            : -1;

        var rule = new PointsRule
        {
            Name = string.Empty,
            Kind = kind,
            TableJson = kind == PointsRuleKind.Table ? "[]" : null,
            Formula = kind == PointsRuleKind.Formula ? string.Empty : null,
            Order = maxOrder + 1
        };
        db.PointsRules.Add(rule);
        await db.SaveChangesAsync(cancellationToken);
        return rule;
    }

    public async Task UpdatePointsRuleAsync(PointsRule rule, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.PointsRules.FirstOrDefaultAsync(r => r.Id == rule.Id, cancellationToken);
        if (existing is null)
            return;

        // Reject a rename that would collide with another rule (case-insensitive); keep the old name.
        var name = (rule.Name ?? string.Empty).Trim();
        if (name.Length > 0)
        {
            var clash = await db.PointsRules.AnyAsync(
                r => r.Id != rule.Id && r.Name == name, cancellationToken);
            if (!clash)
                existing.Name = name;
        }
        else
        {
            existing.Name = string.Empty;
        }

        existing.TableJson = rule.TableJson;
        existing.Formula = rule.Formula;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeletePointsRuleAsync(Guid ruleId, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.PointsRules.FirstOrDefaultAsync(r => r.Id == ruleId, cancellationToken);
        if (existing is null)
            return;

        db.PointsRules.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }

    // ── Rank qualification table ───────────────────────────────────────────────────────────────────────

    public async Task SeedRankQualificationIfEmptyAsync(IReadOnlyList<RankQualificationRow> rows, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.RankQualification.AnyAsync(cancellationToken))
            return;

        db.RankQualification.AddRange(rows);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RankQualificationRow>> GetRankQualificationAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.RankQualification
            .AsNoTracking()
            .OrderBy(r => r.Order)
            .ThenByDescending(r => r.Rank)
            .ToListAsync(cancellationToken);
    }

    public async Task<RankQualificationRow> AddRankQualificationRowAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var maxOrder = await db.RankQualification.AnyAsync(cancellationToken)
            ? await db.RankQualification.MaxAsync(r => r.Order, cancellationToken)
            : -1;

        var row = new RankQualificationRow { Rank = 0, Order = maxOrder + 1 };
        db.RankQualification.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return row;
    }

    public async Task UpdateRankQualificationRowAsync(RankQualificationRow row, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.RankQualification.FirstOrDefaultAsync(r => r.Id == row.Id, cancellationToken);
        if (existing is null)
            return;

        existing.Rank = row.Rank;
        existing.TimeKms = row.TimeKms;
        existing.TimeFirst = row.TimeFirst;
        existing.TimeSecond = row.TimeSecond;
        existing.TimeThird = row.TimeThird;
        existing.TimeThirdJunior = row.TimeThirdJunior;
        existing.PointsKms = row.PointsKms;
        existing.PointsFirst = row.PointsFirst;
        existing.PointsSecond = row.PointsSecond;
        existing.PointsThird = row.PointsThird;
        existing.PointsThirdJunior = row.PointsThirdJunior;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRankQualificationRowAsync(Guid rowId, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.RankQualification.FirstOrDefaultAsync(r => r.Id == rowId, cancellationToken);
        if (existing is null)
            return;

        db.RankQualification.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }
}
