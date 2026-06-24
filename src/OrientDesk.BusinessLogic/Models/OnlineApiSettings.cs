namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// Application-level connection settings for the online live-results service (Supabase). Stored once in
/// the app database and shared across every competition: the project URL, the secret service-role key
/// (write access — kept only locally), the public base URL of the spectator frontend (for generating
/// shareable links), and the publish interval in seconds. Independent of any competition; the
/// per-competition publish options live in <see cref="OnlinePublishSettings"/>.
/// </summary>
public sealed record OnlineApiSettings(
    string SupabaseUrl,
    string ServiceRoleKey,
    string PublicBaseUrl,
    int IntervalSeconds)
{
    /// <summary>The default interval (seconds) between publish ticks when nothing is stored.</summary>
    public const int DefaultIntervalSeconds = 10;

    /// <summary>Smallest sensible publish interval (seconds) — the UI clamps to this.</summary>
    public const int MinIntervalSeconds = 3;

    /// <summary>Empty settings (nothing configured yet).</summary>
    public static readonly OnlineApiSettings Empty =
        new(string.Empty, string.Empty, string.Empty, DefaultIntervalSeconds);

    /// <summary>True when the URL and service-role key are filled in (the minimum to publish).</summary>
    public bool IsReadyToPublish =>
        !string.IsNullOrWhiteSpace(SupabaseUrl) && !string.IsNullOrWhiteSpace(ServiceRoleKey);
}
