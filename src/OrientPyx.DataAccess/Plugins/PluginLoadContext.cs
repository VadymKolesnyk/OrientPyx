using System.Reflection;
using System.Runtime.Loader;

namespace OrientPyx.DataAccess.Plugins;

/// <summary>
/// One plugin's own load context: the plugin and its private dependencies resolve from its folder, while the
/// plugin SDK (and the framework) always come from the application's default context — so the plugin's
/// <see cref="IOrientPyxPlugin"/> is the same type the host knows.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string SdkName = typeof(IOrientPyxPlugin).Assembly.GetName().Name!;

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath)
        : base(Path.GetFileNameWithoutExtension(pluginPath), isCollectible: false) =>
        _resolver = new AssemblyDependencyResolver(pluginPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // null = fall back to the default context (shared SDK, framework, anything the app already ships).
        if (string.Equals(assemblyName.Name, SdkName, StringComparison.OrdinalIgnoreCase))
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
