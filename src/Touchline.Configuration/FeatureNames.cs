namespace Touchline.Configuration;

/// <summary>
/// The feature flags (ADR-062). Each gates one behaviour that already exists, so a flag that is
/// off turns it off and nothing else. The defaults are on, in <c>appsettings.json</c>, so an app
/// with no App Configuration store behaves as it did before the flags.
/// </summary>
public static class FeatureNames
{
    /// <summary>Fans can start matches (<c>POST /api/matches</c>). Off answers 503.</summary>
    public const string FanMatches = nameof(FanMatches);

    /// <summary>Fans can ask the Analyst questions (<c>POST /api/matches/{id}/questions</c>). Off answers 503.</summary>
    public const string FanQa = nameof(FanQa);

    /// <summary>The Analyst's possession search returns results. Off means the tool finds nothing.</summary>
    public const string PossessionSearch = nameof(PossessionSearch);

    /// <summary>The squad calls its models. Off means template lines and fixed answers, as with no Foundry.</summary>
    public const string SquadModels = nameof(SquadModels);

    /// <summary>Every flag, for seeding and tests.</summary>
    public static readonly IReadOnlyList<string> All = [FanMatches, FanQa, PossessionSearch, SquadModels];
}
