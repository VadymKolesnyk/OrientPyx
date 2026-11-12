namespace OrientPyx.Plugins.Sdk;

/// <summary>The plugin API version this SDK implements. A plugin whose <see cref="PluginInfo.MinApiVersion"/> is
/// higher is not loaded. A new version only adds (a plugin written for an older one keeps working); an incompatible
/// change would also bump the assembly's major version.</summary>
/// <remarks>2 — imported participant fields (<see cref="FieldImport"/>) and plugin reports (<see cref="Report"/>).</remarks>
public static class ApiVersion
{
    public const int Current = 2;
}

/// <summary>
/// Entry point of a plugin assembly: one public, parameterless class implementing this interface. The host
/// reads <see cref="Info"/>, then calls <see cref="Register"/> once at startup.
/// </summary>
public interface IOrientPyxPlugin
{
    PluginInfo Info { get; }

    void Register(IPluginBuilder builder);
}

/// <summary>Identity of a plugin. <see cref="Id"/> is stable (it prefixes the plugin's discipline ids and string
/// keys); <see cref="MinApiVersion"/> is the lowest <see cref="ApiVersion.Current"/> it works with.</summary>
public sealed record PluginInfo(
    string Id, string Name, string Version, int MinApiVersion = 1, string? Author = null, string? Description = null);

/// <summary>What a plugin contributes, declared from <see cref="IOrientPyxPlugin.Register"/>.</summary>
public interface IPluginBuilder
{
    /// <summary>Adds a discipline (competition type) to the type dropdowns.</summary>
    void AddDiscipline<T>() where T : Discipline, new();

    /// <summary>Declares an additional participant field the plugin's disciplines read via
    /// <see cref="ParticipantView.Field"/>. It is created in a competition when the plugin's discipline is first used.</summary>
    void AddParticipantField(string key, string label, FieldKind kind);

    /// <summary>Declares an additional participant field that the participant import also fills from files (API 2):
    /// <paramref name="import"/> names the column / XML element it comes from and translates its values. Besides
    /// being created with the plugin's discipline, the field is created by an import that brings values for it.</summary>
    void AddParticipantField(string key, string label, FieldKind kind, FieldImport import) =>
        AddParticipantField(key, label, kind);

    /// <summary>Adds a report (API 2): an item of the plugin's «Плагіни» menu that opens a page with a document the
    /// plugin builds from the competition's data — shown on screen and exported to Word.</summary>
    void AddReport<T>() where T : Report, new()
    {
    }

    /// <summary>Adds translated strings for <paramref name="culture"/> (e.g. "uk-UA"). Labels and names the plugin
    /// declares are looked up here by key; a key with no translation is shown as written.</summary>
    void AddStrings(string culture, IReadOnlyDictionary<string, string> strings);
}

/// <summary>A participant field a plugin declares (see <see cref="IPluginBuilder.AddParticipantField(string, string, FieldKind)"/>).</summary>
public sealed record ParticipantFieldDef(string Key, string Label, FieldKind Kind)
{
    /// <summary>How the participant import fills the field; null when it is not imported from files by name.</summary>
    public FieldImport? Import { get; init; }
}
