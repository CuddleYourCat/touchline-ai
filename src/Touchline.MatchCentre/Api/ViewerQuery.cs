using Microsoft.AspNetCore.Mvc;
using Touchline.Domain.Viewing;

namespace Touchline.MatchCentre.Api;

/// <summary>
/// The viewer's settings, as query parameters on every endpoint that produces words
/// (ADR-027). Bound with <c>[AsParameters]</c>.
/// </summary>
/// <param name="Mode">The viewing mode: <c>casual</c> (the default) or <c>analyst</c>.</param>
/// <param name="Lang">The language, such as <c>en</c> (the default) or <c>es-MX</c>.</param>
/// <param name="Team">The viewer's favourite club ID, built in or fan-built.</param>
/// <param name="Player">The player to follow (player-focus mode).</param>
/// <param name="Metric">The viewer's favourite metric, such as <c>xG</c>.</param>
internal sealed record ViewerQuery(
    [FromQuery(Name = "mode")] string? Mode,
    [FromQuery(Name = "lang")] string? Lang,
    [FromQuery(Name = "team")] string? Team,
    [FromQuery(Name = "player")] string? Player,
    [FromQuery(Name = "metric")] string? Metric)
{
    /// <summary>
    /// Checks the settings against the teams, built in and fan-built.
    /// </summary>
    /// <param name="teams">The team store.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The viewer, or the problems, keyed by query parameter.</returns>
    public async Task<(Viewer? Viewer, IReadOnlyDictionary<string, string[]> Problems)> ResolveAsync(
        ITeamStore teams,
        CancellationToken cancellationToken)
    {
        var parsed = EnumNames.Parse<ViewingMode>(Mode);
        if (Mode is not null && parsed is null)
        {
            return (null, new Dictionary<string, string[]> { ["mode"] = ["The mode must be casual or analyst."] });
        }

        var mode = parsed ?? ViewingMode.Casual;

        // Looked up once here, because Viewer.Create's checks are synchronous.
        var teamKnown = Team is null || await teams.GetAsync(Team, cancellationToken) is not null;
        var playerKnown = Player is null || await teams.PlayerExistsAsync(Player, cancellationToken);
        return Viewer.Create(mode, Lang, Team, Player, Metric, _ => teamKnown, _ => playerKnown);
    }
}
