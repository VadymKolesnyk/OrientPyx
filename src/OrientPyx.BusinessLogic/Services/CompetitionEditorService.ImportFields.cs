using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

// The additional fields a participant import fills (Р-59…Р-62): the competition's own fields and the ones switched-on
// plugins declare, taken from a CSV column or an XML element by name, with the plugin's value translations.
public sealed partial class CompetitionEditorService
{
    public async Task<IReadOnlyList<ImportField>> GetImportFieldsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return [];

        var stored = await GetCustomFieldsAsync(cancellationToken);
        var declared = DeclaredImportFields();
        var fields = new List<ImportField>();
        foreach (var field in stored)
        {
            // A plugin field keeps the plugin's import names while the plugin is on — so does a field of the user's
            // that a plugin shares by its key (Р-17); any field also goes by its own name.
            (ParticipantFieldDef? Def, string? PluginName) plugin =
                field.PluginId is { } id && declared.TryGetValue((id, field.Key), out var own)
                    ? own
                    : declared.Values.FirstOrDefault(d => d.Def.Import is not null && d.Def.Key == field.Key);
            fields.Add(new ImportField(field, plugin.Def?.Import, plugin.Def is null ? null : plugin.PluginName, Exists: true));
        }

        var keys = stored.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var order = stored.Count == 0 ? 0 : stored.Max(f => f.Order);
        foreach (var ((pluginId, _), (def, pluginName)) in declared)
        {
            if (def.Import is null || !keys.Add(def.Key))
                continue;
            fields.Add(new ImportField(NewPluginField(pluginId, def, ++order), def.Import, pluginName, Exists: false));
        }
        return fields;
    }

    public async Task<IReadOnlyList<ImportFileField>> GetImportFileFieldsAsync(
        UofParticipantData data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (_session.CurrentEvent is null || data.Participants.All(p => p.Raw.Count == 0))
            return [];

        var found = new List<ImportFileField>();
        foreach (var field in await GetImportFieldsAsync(cancellationToken))
        {
            string? source = null;
            var count = 0;
            foreach (var participant in data.Participants)
            {
                if (field.RawValue(participant) is not { } value)
                    continue;
                source ??= value.Name;
                count++;
            }
            if (source is not null)
                found.Add(new ImportFileField(field.Key, field.Field.Label, field.PluginName, source, count, field.Exists));
        }
        return found;
    }

    // The import's view of the file: the chosen XML values moved into Fields (a value the file already maps — a CSV
    // column — wins), the plugin translations applied («0» → «Ж»), the raw values dropped. Also returns the fields
    // the import knows, the plugin ones not created yet included.
    private async Task<(UofParticipantData Data, IReadOnlyList<ImportField> Fields)> PrepareImportFieldsAsync(
        UofParticipantData data, CancellationToken cancellationToken)
    {
        var fields = await GetImportFieldsAsync(cancellationToken);
        var byKey = fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var fromFile = data.FileFieldKeys
            .Select(k => byKey.GetValueOrDefault(k))
            .OfType<ImportField>()
            .ToList();

        var participants = new List<UofParticipant>(data.Participants.Count);
        foreach (var participant in data.Participants)
        {
            var values = new Dictionary<string, string>(participant.Fields, StringComparer.Ordinal);
            foreach (var field in fromFile)
            {
                if (values.TryGetValue(field.Key, out var mapped) && mapped.Trim().Length > 0)
                    continue;
                if (field.RawValue(participant) is { } raw)
                    values[field.Key] = raw.Value;
            }
            foreach (var key in values.Keys.ToList())
                if (byKey.TryGetValue(key, out var field))
                    values[key] = field.Translate(values[key]);

            participants.Add(participant with { Fields = values, Raw = new Dictionary<string, string>() });
        }

        return (data with { Participants = participants, FileFieldKeys = [] }, fields);
    }

    // Creates the plugin fields the file brings values for that the competition does not have yet (Р-60).
    private async Task CreateImportedPluginFieldsAsync(
        UofParticipantData data, IReadOnlyList<ImportField> fields, CancellationToken cancellationToken)
    {
        foreach (var field in fields.Where(f => !f.Exists))
        {
            var hasValue = data.Participants.Any(p =>
                p.Fields.TryGetValue(field.Key, out var value) && value.Trim().Length > 0);
            if (hasValue)
                await _eventStore.AddCustomFieldDefAsync(FolderPath, field.Field.ToDef(), cancellationToken);
        }
    }

    // The fields switched-on plugins declare, by (plugin id, field key), with the plugin's name.
    private Dictionary<(string PluginId, string Key), (ParticipantFieldDef Def, string PluginName)> DeclaredImportFields()
    {
        var declared = new Dictionary<(string, string), (ParticipantFieldDef, string)>();
        if (_plugins is null)
            return declared;
        foreach (var plugin in _plugins.Plugins.Where(p => _plugins.IsEnabled(p.Info.Id)))
            foreach (var def in plugin.Fields)
                declared.TryAdd((plugin.Info.Id, def.Key), (def, plugin.Info.Name));
        return declared;
    }

    // The competition's copy of a field a plugin declares (Р-17).
    private static CustomField NewPluginField(string pluginId, ParticipantFieldDef declared, int order) => new(
        declared.Key,
        string.IsNullOrWhiteSpace(declared.Label) ? declared.Key : declared.Label.Trim(),
        CustomField.KindOf(declared.Kind),
        declared.Kind.Options,
        CustomField.PluginSourcePrefix + pluginId,
        order);
}
