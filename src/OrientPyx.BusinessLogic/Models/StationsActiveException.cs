namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Refusal to rename a competition's identifier while network stations work on it: they address the competition by
/// that identifier, so renaming it under them would strand their session. The page says so in the UI language.
/// </summary>
public sealed class StationsActiveException(IReadOnlyList<string> stations)
    : InvalidOperationException($"Stations are working on this competition: {string.Join(", ", stations)}.")
{
    public IReadOnlyList<string> Stations { get; } = stations;
}
