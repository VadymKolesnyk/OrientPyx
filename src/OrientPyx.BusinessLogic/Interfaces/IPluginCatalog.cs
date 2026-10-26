namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// The plugins found at startup (implemented in DataAccess, which loads the assemblies). A disabled plugin is listed
/// with <see cref="LoadedPlugin.Enabled"/> false and contributes nothing; a plugin that failed to load is listed in
/// <see cref="Errors"/>. Plugins are read once per launch — there is no hot reload.
/// </summary>
public interface IPluginCatalog
{
    /// <summary>Every plugin that loaded (enabled or disabled), one per id.</summary>
    IReadOnlyList<LoadedPlugin> Plugins { get; }

    /// <summary>Plugins that could not be loaded, with the reason.</summary>
    IReadOnlyList<PluginLoadError> Errors { get; }

    /// <summary>The folder for the user's own plugins (under the data root; survives updates).</summary>
    string UserFolder { get; }

    /// <summary>The scanned folders, lowest priority first (Р-12).</summary>
    IReadOnlyList<string> Folders { get; }
}

/// <summary>A loaded plugin and what it contributes.</summary>
/// <param name="Folder">The folder the plugin was loaded from.</param>
/// <param name="Enabled">False when the user switched the plugin off (it then contributes nothing, Р-12).</param>
/// <param name="Strings">Translated strings by culture name, then by the plugin's own key.</param>
public sealed record LoadedPlugin(
    PluginInfo Info,
    string Folder,
    bool Enabled,
    IReadOnlyList<Discipline> Disciplines,
    IReadOnlyList<ParticipantFieldDef> Fields,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Strings);

/// <summary>A plugin that could not be loaded.</summary>
/// <param name="PluginId">The plugin id when it was readable; null otherwise.</param>
/// <param name="Path">The plugin file or folder.</param>
public sealed record PluginLoadError(string? PluginId, string Path, string Message);
