using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

// Additional participant fields (Р-13…Р-19) and the CSV/Excel mapping templates.
public sealed partial class CompetitionEditorService
{
    public async Task<IReadOnlyList<CustomField>> GetCustomFieldsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return [];

        // A plugin field appears as soon as its discipline is used (Р-17) — ensured here too, so a competition
        // that picked the discipline before this version (or through an import) gets its fields.
        await EnsurePluginFieldsAsync(cancellationToken);
        var defs = await _eventStore.GetCustomFieldDefsAsync(FolderPath, cancellationToken);
        return defs.Select(CustomField.From).ToList();
    }

    public async Task<CustomField?> AddCustomFieldAsync(
        string label, CustomFieldKind kind, IReadOnlyList<ChoiceOption> options, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return null;

        var name = (label ?? string.Empty).Trim();
        if (name.Length == 0)
            return null;

        var folder = FolderPath;
        var existing = await _eventStore.GetCustomFieldDefsAsync(folder, cancellationToken);
        var taken = existing.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseKey = CustomField.KeyFromLabel(name);
        var key = baseKey;
        for (var i = 2; taken.Contains(key); i++)
            key = $"{baseKey}-{i}";

        var field = new CustomField(key, name, kind, kind == CustomFieldKind.Choice ? options : [],
            CustomField.UserSource, existing.Count == 0 ? 1 : existing.Max(f => f.Order) + 1);
        return await _eventStore.AddCustomFieldDefAsync(folder, field.ToDef(), cancellationToken) ? field : null;
    }

    public async Task UpdateCustomFieldAsync(CustomField field, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (_session.CurrentEvent is null)
            return;

        var label = field.Label.Trim();
        if (label.Length == 0)
            return;
        await _eventStore.UpdateCustomFieldDefAsync(FolderPath, (field with { Label = label }).ToDef(), cancellationToken);
    }

    public async Task<bool> DeleteCustomFieldAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return false;

        // A plugin's field stays while the plugin's discipline is in use (Р-15).
        var defs = await _eventStore.GetCustomFieldDefsAsync(FolderPath, cancellationToken);
        var def = defs.FirstOrDefault(f => f.Key == key);
        if (def is null)
            return false;
        if (CustomField.From(def).PluginId is { } pluginId
            && (await GetUsedPluginIdsAsync(cancellationToken)).Contains(pluginId))
            return false;

        await _eventStore.DeleteCustomFieldDefAsync(FolderPath, key, cancellationToken);
        return true;
    }

    public async Task<bool> IsCustomFieldLockedAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return false;
        var defs = await _eventStore.GetCustomFieldDefsAsync(FolderPath, cancellationToken);
        return defs.FirstOrDefault(f => f.Key == key) is { } def
            && CustomField.From(def).PluginId is { } pluginId
            && (await GetUsedPluginIdsAsync(cancellationToken)).Contains(pluginId);
    }

    public async Task<string> SetParticipantFieldAsync(
        Guid participantId, string key, string? value, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return value ?? string.Empty;

        var folder = FolderPath;
        var defs = await _eventStore.GetCustomFieldDefsAsync(folder, cancellationToken);
        var def = defs.FirstOrDefault(f => f.Key == key);
        var normalized = def is null ? (value ?? string.Empty).Trim() : CustomField.From(def).Normalize(value);
        await _eventStore.SetParticipantFieldValueAsync(folder, participantId, key, normalized, cancellationToken);
        return normalized;
    }

    public async Task EnsurePluginFieldsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || _plugins is null)
            return;

        var used = await GetUsedPluginIdsAsync(cancellationToken);
        if (used.Count == 0)
            return;

        var folder = FolderPath;
        var defs = await _eventStore.GetCustomFieldDefsAsync(folder, cancellationToken);
        var keys = defs.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var order = defs.Count == 0 ? 0 : defs.Max(f => f.Order);
        foreach (var plugin in _plugins.Plugins.Where(p => _plugins.IsEnabled(p.Info.Id) && used.Contains(p.Info.Id)))
        {
            foreach (var declared in plugin.Fields)
            {
                // Created once (Р-17); an existing field with the key — the user's own or an earlier copy — is kept.
                if (!keys.Add(declared.Key))
                    continue;
                var field = new CustomField(
                    declared.Key,
                    string.IsNullOrWhiteSpace(declared.Label) ? declared.Key : declared.Label.Trim(),
                    CustomField.KindOf(declared.Kind),
                    declared.Kind.Options,
                    CustomField.PluginSourcePrefix + plugin.Info.Id,
                    ++order);
                await _eventStore.AddCustomFieldDefAsync(folder, field.ToDef(), cancellationToken);
            }
        }
    }

    // The plugins whose disciplines the competition uses: any day's default or any group override.
    private async Task<HashSet<string>> GetUsedPluginIdsAsync(CancellationToken cancellationToken)
    {
        var folder = FolderPath;
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var day in await _eventStore.GetDaysAsync(folder, cancellationToken))
        {
            if (_strategies.For(day.DefaultDiscipline).PluginId is { } dayPlugin)
                used.Add(dayPlugin);
            foreach (var settings in await _eventStore.GetGroupDaySettingsAsync(folder, day.Id, cancellationToken))
                if (settings.DisciplineOverride is { } id && _strategies.For(id).PluginId is { } groupPlugin)
                    used.Add(groupPlugin);
        }
        return used;
    }

    public async Task<string?> GetImportMappingAsync(string headerSetHash, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is not null
            && await _eventStore.GetImportMappingJsonAsync(FolderPath, headerSetHash, cancellationToken) is { Length: > 0 } own)
            return own;

        // No mapping of the competition's own for this layout: seed from the application default.
        var json = await _appStore.GetImportMappingsJsonAsync(cancellationToken);
        return ImportMappingDefaults.Get(json, headerSetHash);
    }

    public async Task SaveImportMappingAsync(string headerSetHash, string json, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is not null)
            await _eventStore.SaveImportMappingJsonAsync(FolderPath, headerSetHash, json, cancellationToken);

        var defaults = await _appStore.GetImportMappingsJsonAsync(cancellationToken);
        await _appStore.SaveImportMappingsJsonAsync(ImportMappingDefaults.Set(defaults, headerSetHash, json), cancellationToken);
    }

    // Every participant's field values, grouped for the row builders.
    private async Task<Dictionary<Guid, Dictionary<string, string>>> LoadFieldValuesAsync(
        string folder, CancellationToken cancellationToken)
    {
        var values = await _eventStore.GetParticipantFieldValuesAsync(folder, cancellationToken);
        return values
            .GroupBy(v => v.ParticipantId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(v => v.Key, v => v.Value));
    }

    private static IReadOnlyDictionary<string, string> FieldsOf(
        Dictionary<Guid, Dictionary<string, string>> values, Guid participantId) =>
        values.TryGetValue(participantId, out var fields) ? fields : new Dictionary<string, string>();
}
