namespace OrientPyx.Tests.DocShots;

/// <summary>
/// A documentation-screenshot scenario. Skipped unless <c>ORIENTPYX_DOCSHOTS</c> names the site's content folder
/// (<c>website/content</c>), so the regular suite never renders them; <c>scripts/docs.ps1 -Shots</c> sets it.
/// </summary>
public sealed class DocShotFactAttribute : FactAttribute
{
    public DocShotFactAttribute()
    {
        if (DocShotOutput.Root is null)
            Skip = "Documentation screenshots: set ORIENTPYX_DOCSHOTS to website/content (scripts/docs.ps1 -Shots).";
    }
}

/// <summary>Where the screenshots (<c>shots/</c>) and the demo archives (<c>demo/</c>) go.</summary>
internal static class DocShotOutput
{
    public static string? Root { get; } =
        Environment.GetEnvironmentVariable("ORIENTPYX_DOCSHOTS") is { Length: > 0 } dir ? Path.GetFullPath(dir) : null;

    public static string ShotsDir => Path.Combine(Root ?? throw new InvalidOperationException("ORIENTPYX_DOCSHOTS is not set."), "shots");

    public static string DemoDir => Path.Combine(Root ?? throw new InvalidOperationException("ORIENTPYX_DOCSHOTS is not set."), "demo");
}
