namespace OrientPyx.DataAccess.Plugins;

/// <summary>
/// Where plugin candidates come from. Today only compiled DLLs (<see cref="DllPluginSource"/>); a source-code
/// folder compiled at startup could be another implementation later.
/// </summary>
internal interface IPluginSource
{
    /// <summary>The candidate plugin files in <paramref name="folder"/>.</summary>
    IEnumerable<string> Candidates(string folder);
}

/// <summary>A compiled plugin: <c>&lt;folder&gt;/&lt;Name&gt;/&lt;Name&gt;.dll</c> (the layout the plugin build produces).</summary>
internal sealed class DllPluginSource : IPluginSource
{
    public IEnumerable<string> Candidates(string folder)
    {
        if (!Directory.Exists(folder))
            yield break;

        foreach (var dir in Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            var dll = Path.Combine(dir, Path.GetFileName(dir) + ".dll");
            if (File.Exists(dll))
                yield return dll;
        }
    }
}
