namespace OrientPyx.Plugins.Sdk;

/// <summary>Who a discipline's result belongs to (see <see cref="IDisciplineStrategy.Competitors"/>).</summary>
public enum CompetitorModel
{
    /// <summary>Every runner is ranked on their own.</summary>
    Individual,

    /// <summary>Runners sharing a team name in a group are scored and placed together, without an order of
    /// legs (rogaine).</summary>
    Team
}
