namespace OrientPyx.Plugins.Sdk;

/// <summary>The plugin API version this SDK implements. A plugin whose <see cref="PluginInfo.MinApiVersion"/> is
/// higher is not loaded. A minor change only adds; an incompatible change bumps the assembly's major version.</summary>
public static class ApiVersion
{
    public const int Current = 1;
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

    /// <summary>Adds translated strings for <paramref name="culture"/> (e.g. "uk-UA"). Labels and names the plugin
    /// declares are looked up here by key; a key with no translation is shown as written.</summary>
    void AddStrings(string culture, IReadOnlyDictionary<string, string> strings);
}

/// <summary>A participant field a plugin declares (see <see cref="IPluginBuilder.AddParticipantField"/>).</summary>
public sealed record ParticipantFieldDef(string Key, string Label, FieldKind Kind);
