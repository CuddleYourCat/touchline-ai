using System.Globalization;
using System.Text.RegularExpressions;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;
using Touchline.MatchCentre.Api;

namespace Touchline.MatchCentre.TeamBuilding;

/// <summary>
/// Checks a fan's team draft against the squad rules and name format (ADR-031). Problems are
/// keyed by the JSON path of the field at fault, such as <c>squad[3].name</c>, so the UI can
/// show each one beside its field.
/// </summary>
internal static partial class TeamDraftValidator
{
    /// <summary>
    /// Checks a draft.
    /// </summary>
    /// <param name="draft">The draft.</param>
    /// <param name="existingPlayerIds">The IDs of the team's current players when updating; empty when creating.</param>
    /// <returns>The problems by field path, and every name to pass to the name guard, by field path.</returns>
    public static (Dictionary<string, string[]> Problems, List<(string Path, NameToCheck Name)> Names) Check(
        TeamDraft draft,
        IReadOnlySet<string> existingPlayerIds)
    {
        var problems = new Dictionary<string, List<string>>();
        var names = new List<(string Path, NameToCheck Name)>();
        void Problem(string path, string message)
        {
            if (!problems.TryGetValue(path, out var list))
            {
                problems[path] = list = [];
            }

            list.Add(message);
        }

        void Name(string path, NameKind kind, string? value)
        {
            if (NameRules.FormatProblem(value) is { } problem)
            {
                Problem(path, problem);
            }
            else
            {
                names.Add((path, new NameToCheck(kind, value!)));
            }
        }

        Name("name", NameKind.Team, draft.Name);
        Name("venue", NameKind.Venue, draft.Venue);

        if (draft.ShortName is null || !ShortName().IsMatch(draft.ShortName))
        {
            Problem("shortName", "A short name is 2 to 4 capital letters.");
        }
        else
        {
            names.Add(("shortName", new NameToCheck(NameKind.Abbreviation, draft.ShortName)));
        }

        if (draft.Colours is null)
        {
            Problem("colours", "Colours are required.");
        }
        else
        {
            Colour("colours.primary", draft.Colours.Primary, Problem);
            Colour("colours.secondary", draft.Colours.Secondary, Problem);
            if (string.Equals(draft.Colours.Primary, draft.Colours.Secondary, StringComparison.OrdinalIgnoreCase))
            {
                Problem("colours.secondary", "The two kit colours must differ.");
            }
        }

        if (draft.Crest is null)
        {
            Problem("crest", "A crest is required.");
        }
        else
        {
            if (!Enum.IsDefined(draft.Crest.Shape))
            {
                Problem("crest.shape", "That crest shape isn't offered.");
            }

            if (!Enum.IsDefined(draft.Crest.Emblem))
            {
                Problem("crest.emblem", "That emblem isn't offered.");
            }

            if (draft.Crest.Initials is null || !Initials().IsMatch(draft.Crest.Initials))
            {
                Problem("crest.initials", "Initials are up to 3 capital letters.");
            }
            else if (draft.Crest.Initials.Length > 0)
            {
                // Initials could spell a real club's, so they go to the name guard like any name.
                names.Add(("crest.initials", new NameToCheck(NameKind.Abbreviation, draft.Crest.Initials)));
            }

            Colour("crest.primary", draft.Crest.Primary, Problem);
            Colour("crest.secondary", draft.Crest.Secondary, Problem);
        }

        if (draft.Manager is null)
        {
            Problem("manager", "A manager is required.");
        }
        else
        {
            Name("manager.name", NameKind.Manager, draft.Manager.Name);
            if (!Enum.IsDefined(draft.Manager.Style))
            {
                Problem("manager.style", "That style isn't offered.");
            }
        }

        CheckSquad(draft.Squad ?? [], existingPlayerIds, Problem, Name);

        return (problems.ToDictionary(p => p.Key, p => p.Value.ToArray()), names);
    }

    private static void CheckSquad(
        IReadOnlyList<PlayerDraft> squad,
        IReadOnlySet<string> existingPlayerIds,
        Action<string, string> problem,
        Action<string, NameKind, string?> name)
    {
        if (squad.Count is < SquadRules.MinPlayers or > SquadRules.MaxPlayers)
        {
            problem("squad", $"A squad has {SquadRules.MinPlayers} to {SquadRules.MaxPlayers} players.");
        }

        for (var i = 0; i < squad.Count; i++)
        {
            var path = string.Create(CultureInfo.InvariantCulture, $"squad[{i}]");
            var player = squad[i];
            if (player is null)
            {
                problem(path, "A player is required.");
                continue;
            }

            name($"{path}.name", NameKind.Player, player.Name);

            if (player.ShirtNumber is < SquadRules.MinShirtNumber or > SquadRules.MaxShirtNumber)
            {
                problem($"{path}.shirtNumber", $"A shirt number is {SquadRules.MinShirtNumber} to {SquadRules.MaxShirtNumber}.");
            }

            if (!Enum.IsDefined(player.Archetype))
            {
                problem($"{path}.archetype", "That archetype isn't offered.");
            }

            if (!Enum.IsDefined(player.PreferredFoot))
            {
                problem($"{path}.preferredFoot", "The preferred foot is Left, Right or Both.");
            }

            if (player.Id is not null && !existingPlayerIds.Contains(player.Id))
            {
                problem($"{path}.id", "That player isn't in this team. Leave the ID out for a new player.");
            }
        }

        var players = squad.Where(p => p is not null).ToList();
        foreach (var shirt in players.GroupBy(p => p.ShirtNumber).Where(g => g.Count() > 1))
        {
            problem("squad", string.Create(CultureInfo.InvariantCulture, $"Shirt number {shirt.Key} is used more than once."));
        }

        foreach (var id in players.Where(p => p.Id is not null).GroupBy(p => p.Id).Where(g => g.Count() > 1))
        {
            problem("squad", $"Player '{id.Key}' appears more than once.");
        }

        var positions = players.Where(p => Enum.IsDefined(p.Archetype)).Select(p => Archetypes.PositionOf(p.Archetype)).ToList();
        foreach (var (position, minimum) in SquadRules.MinimumByPosition)
        {
            var count = positions.Count(p => p == position);
            if (count < minimum)
            {
                problem("squad", string.Create(CultureInfo.InvariantCulture,
                    $"A squad needs at least {minimum} {Plural(position)}; this one has {count}."));
            }
        }
    }

    private static string Plural(PlayerPosition position) => position switch
    {
        PlayerPosition.Goalkeeper => "goalkeepers",
        PlayerPosition.Defender => "defenders",
        PlayerPosition.Midfielder => "midfielders",
        _ => "forwards"
    };

    private static void Colour(string path, string? value, Action<string, string> problem)
    {
        if (value is null || !HexColour().IsMatch(value))
        {
            problem(path, "A colour is #rrggbb hex.");
        }
    }

    [GeneratedRegex("^[A-Z]{2,4}$")]
    private static partial Regex ShortName();

    [GeneratedRegex("^[A-Z]{0,3}$")]
    private static partial Regex Initials();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColour();
}
