using System.Reflection;
using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.DataAccess.Plugins;

/// <summary>
/// Loads the plugins from a list of folders (lowest priority first). Each plugin assembly gets its own
/// <see cref="PluginLoadContext"/>; its <see cref="IOrientPyxPlugin"/> is instantiated and asked to register.
/// Anything going wrong — a broken file, an incompatible API version, an exception in <c>Register</c> — skips that
/// plugin with a <see cref="PluginLoadError"/>; loading never throws. A plugin id met again in a later folder
/// replaces the earlier one (Р-12).
/// </summary>
internal sealed class PluginLoader
{
    private readonly IPluginSource _source;

    public PluginLoader(IPluginSource source) => _source = source;

    public (IReadOnlyList<LoadedPlugin> Plugins, IReadOnlyList<PluginLoadError> Errors) Load(
        IReadOnlyList<string> folders)
    {
        var byId = new Dictionary<string, LoadedPlugin>(StringComparer.Ordinal);
        var order = new List<string>();
        var errors = new List<PluginLoadError>();

        foreach (var folder in folders)
            foreach (var path in _source.Candidates(folder))
            {
                var plugin = TryLoad(path, errors);
                if (plugin is null)
                    continue;
                if (!byId.ContainsKey(plugin.Info.Id))
                    order.Add(plugin.Info.Id);
                byId[plugin.Info.Id] = plugin;
            }

        return (order.Select(id => byId[id]).ToList(), errors);
    }

    private static LoadedPlugin? TryLoad(string path, List<PluginLoadError> errors)
    {
        string? id = null;
        try
        {
            var context = new PluginLoadContext(path);
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
            var entryType = FindEntryType(assembly)
                ?? throw new InvalidOperationException($"No public class implementing {nameof(IOrientPyxPlugin)}.");
            var plugin = (IOrientPyxPlugin)Activator.CreateInstance(entryType)!;
            var info = plugin.Info;
            id = info.Id;

            if (string.IsNullOrWhiteSpace(info.Id))
                throw new InvalidOperationException("The plugin id is empty.");
            if (info.MinApiVersion > ApiVersion.Current)
                throw new InvalidOperationException(
                    $"Needs plugin API {info.MinApiVersion}, the application provides {ApiVersion.Current}.");

            // Every plugin registers, on or off: switching one on later takes effect without a restart, and an off
            // plugin's contributions are simply not used (IPluginCatalog.IsEnabled).
            var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? path;
            var builder = new Builder();
            plugin.Register(builder);
            return new LoadedPlugin(info, folder, builder.Disciplines, builder.Fields, builder.Strings);
        }
        catch (Exception ex)
        {
            var message = ex is ReflectionTypeLoadException rtl && rtl.LoaderExceptions.FirstOrDefault() is { } inner
                ? inner.Message
                : ex.Message;
            errors.Add(new PluginLoadError(id, path, message));
            return null;
        }
    }

    private static Type? FindEntryType(Assembly assembly) =>
        assembly.GetExportedTypes().FirstOrDefault(t =>
            t is { IsClass: true, IsAbstract: false }
            && typeof(IOrientPyxPlugin).IsAssignableFrom(t)
            && t.GetConstructor(Type.EmptyTypes) is not null);

    private sealed class Builder : IPluginBuilder
    {
        private readonly Dictionary<string, Dictionary<string, string>> _strings = new(StringComparer.OrdinalIgnoreCase);

        public List<Discipline> Disciplines { get; } = [];

        public List<ParticipantFieldDef> Fields { get; } = [];

        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Strings =>
            _strings.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string>)kv.Value,
                StringComparer.OrdinalIgnoreCase);

        public void AddDiscipline<T>() where T : Discipline, new()
        {
            var discipline = new T();
            if (string.IsNullOrWhiteSpace(discipline.Id))
                throw new InvalidOperationException($"Discipline {typeof(T).Name} has an empty id.");
            if (Disciplines.Any(d => d.Id == discipline.Id))
                throw new InvalidOperationException($"Discipline id '{discipline.Id}' is registered twice.");
            Disciplines.Add(discipline);
        }

        public void AddParticipantField(string key, string label, FieldKind kind)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("A field key is required.", nameof(key));
            Fields.Add(new ParticipantFieldDef(key.Trim(), label, kind));
        }

        public void AddStrings(string culture, IReadOnlyDictionary<string, string> strings)
        {
            if (!_strings.TryGetValue(culture, out var dict))
                _strings[culture] = dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in strings)
                dict[key] = value;
        }
    }
}
