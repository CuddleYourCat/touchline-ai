using Touchline.Domain.Teams;

namespace Touchline.Search;

/// <summary>
/// Where the indexer finds team profiles to name players from (ADR-056). A port so lane H can swap
/// the embedded catalogue for the fan-built teams store without touching the indexer.
/// </summary>
public interface ITeamProfiles
{
    /// <summary>Every known team profile, by team ID.</summary>
    IReadOnlyDictionary<string, TeamProfile> All { get; }
}

/// <summary>The default <see cref="ITeamProfiles"/>: the catalogue embedded in the Domain assembly.</summary>
public sealed class EmbeddedTeamProfiles : ITeamProfiles
{
    private readonly Lazy<IReadOnlyDictionary<string, TeamProfile>> _all = new(() => TeamProfileCatalog.LoadEmbedded());

    /// <inheritdoc />
    public IReadOnlyDictionary<string, TeamProfile> All => _all.Value;
}
