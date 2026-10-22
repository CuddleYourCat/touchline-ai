using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;
using Touchline.MatchCentre.Api;

namespace Touchline.MatchCentre.TeamBuilding;

/// <summary>
/// Turns built-in profiles and fan drafts into the API's team view (ADR-031).
/// </summary>
internal static class TeamViews
{
    /// <summary>Every fan-built team's ID starts with this, so it can't collide with a built-in team.</summary>
    public const string FanPrefix = "fan-";

    /// <summary>Where a team's crest is served from.</summary>
    /// <param name="teamId">The team.</param>
    /// <returns>The relative URL.</returns>
    public static string CrestUrl(string teamId) => $"/api/teams/{Uri.EscapeDataString(teamId)}/crest.svg";

    /// <summary>
    /// The view of a built-in team. Its crest is derived from its colours and short name, and it
    /// has no manager until the profiles name one.
    /// </summary>
    /// <param name="team">The profile.</param>
    /// <returns>The view, at version 1.</returns>
    public static TeamDetail FromProfile(TeamProfile team)
    {
        ArgumentNullException.ThrowIfNull(team);
        var initials = new string([.. team.ShortName.Where(char.IsAsciiLetterUpper).Take(3)]);
        return new TeamDetail(
            new TeamSummary(team.Id, team.Name, team.ShortName, team.Venue, team.Colours, TeamOrigin.BuiltIn, CrestUrl(team.Id)),
            null,
            new CrestSpec(CrestShape.Shield, CrestEmblem.Ball, initials, team.Colours.Primary, team.Colours.Secondary),
            [.. team.Squad.OrderBy(p => p.ShirtNumber).Select(p => PlayerSummary.From(p, team.Id))],
            1);
    }

    /// <summary>A new ID for a fan-built team: a slug of its name and a random suffix.</summary>
    /// <param name="name">The team's name.</param>
    /// <returns>For example <c>fan-riverside-rovers-k3x9qa</c>.</returns>
    public static string NewTeamId(string name)
    {
        var slug = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var trimmed = slug.ToString().Trim('-');
        trimmed = trimmed.Length > 20 ? trimmed[..20].TrimEnd('-') : trimmed;
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        var suffix = new string([.. Enumerable.Range(0, 6).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)])]);
        return trimmed.Length == 0 ? $"{FanPrefix}{suffix}" : $"{FanPrefix}{trimmed}-{suffix}";
    }

    /// <summary>
    /// Builds a fan team's view from a valid draft. Players who already exist keep their IDs;
    /// new players get the next free <c>{teamId}-p{n}</c>, never reusing a number, so a
    /// player's history can't pass to someone else.
    /// </summary>
    /// <param name="draft">The draft, already validated.</param>
    /// <param name="teamId">The team's ID.</param>
    /// <param name="previous">The team's current view when updating, or null when creating.</param>
    /// <returns>The view, one version on from <paramref name="previous"/>.</returns>
    public static TeamDetail FromDraft(TeamDraft draft, string teamId, TeamDetail? previous)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var next = (previous?.Squad ?? []).Select(p => PlayerNumber(p.Id, teamId)).DefaultIfEmpty(0).Max() + 1;
        var squad = new List<PlayerSummary>();
        foreach (var player in draft.Squad)
        {
            var id = player.Id ?? string.Create(CultureInfo.InvariantCulture, $"{teamId}-p{next++}");
            squad.Add(new PlayerSummary(id, player.Name, player.ShirtNumber, Archetypes.PositionOf(player.Archetype), teamId, player.Archetype, player.PreferredFoot));
        }

        return new TeamDetail(
            new TeamSummary(teamId, draft.Name, draft.ShortName, draft.Venue, draft.Colours, TeamOrigin.Fan, CrestUrl(teamId)),
            draft.Manager,
            draft.Crest,
            [.. squad.OrderBy(p => p.ShirtNumber)],
            (previous?.Version ?? 0) + 1);
    }

    private static int PlayerNumber(string playerId, string teamId) =>
        playerId.StartsWith($"{teamId}-p", StringComparison.Ordinal)
        && int.TryParse(playerId.AsSpan(teamId.Length + 2), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;
}
