namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// The plugins found at startup (implemented in DataAccess, which loads the assemblies) and which of them the user
/// switched on («Плагіни»). Every plugin is off until switched on; switching takes effect at once — an off plugin
/// contributes nothing (its disciplines resolve as unknown, its fields are not created). A plugin that failed to load
/// is listed in <see cref="Errors"/>. The plugin files are read once per launch — there is no hot reload of DLLs.
/// </summary>
public interface IPluginCatalog
{
    /// <summary>Every plugin that loaded (on or off), one per id.</summary>
    IReadOnlyList<LoadedPlugin> Plugins { get; }

    /// <summary>Plugins that could not be loaded, with the reason.</summary>
    IReadOnlyList<PluginLoadError> Errors { get; }

    /// <summary>
    /// The one plugin folder (under the data root; survives updates): the user's own plugins plus the standard ones,
    /// which are copied in from the app package at startup.
    /// </summary>
    string UserFolder { get; }

    /// <summary>The scanned folders (just <see cref="UserFolder"/>).</summary>
    IReadOnlyList<string> Folders { get; }

    /// <summary>True when the plugin is loaded and switched on.</summary>
    bool IsEnabled(string pluginId);

    /// <summary>Switches a plugin on or off and remembers the choice in the app database.</summary>
    Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Raised after <see cref="SetEnabledAsync"/> changed a plugin's state.</summary>
    event EventHandler? EnabledChanged;
}

/// <summary>A loaded plugin and what it contributes (whether it is switched on is <see cref="IPluginCatalog.IsEnabled"/>).</summary>
/// <param name="Folder">The folder the plugin was loaded from.</param>
/// <param name="Strings">Translated strings by culture name, then by the plugin's own key.</param>
public sealed record LoadedPlugin(
    PluginInfo Info,
    string Folder,
    IReadOnlyList<Discipline> Disciplines,
    IReadOnlyList<ParticipantFieldDef> Fields,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Strings)
{
    /// <summary>The plugin's reports (<see cref="IPluginBuilder.AddReport{T}"/>), in registration order.</summary>
    public IReadOnlyList<Report> Reports { get; init; } = [];
}

/// <summary>A plugin that could not be loaded.</summary>
/// <param name="PluginId">The plugin id when it was readable; null otherwise.</param>
/// <param name="Path">The plugin file or folder.</param>
public sealed record PluginLoadError(string? PluginId, string Path, string Message);
