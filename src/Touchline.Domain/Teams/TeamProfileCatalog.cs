using System.Globalization;

namespace Touchline.Domain.Teams;

/// <summary>
/// Loads the team profiles shipped with the domain as embedded JSON files, and checks
/// each one is usable before the simulator ever sees it. They're read and validated once,
/// on first use, then shared.
/// </summary>
public static class TeamProfileCatalog
{
    private const string _resourcePrefix = "Touchline.Domain.Teams.Profiles.";
    private const int _minimumSquadSize = 11;
    private const double _minimumTopSpeedKmh = 20;
    private const double _maximumTopSpeedKmh = 40;

    // Lazy<T> is thread-safe by default, so concurrent first calls load the files once.
    private static readonly Lazy<IReadOnlyDictionary<string, TeamProfile>> _embedded = new(LoadAllEmbedded);

    /// <summary>
    /// The validated embedded team profiles, loaded on first use.
    /// </summary>
    /// <returns>The profiles, keyed by team ID.</returns>
    /// <exception cref="InvalidDataException">A profile is missing, malformed or fails validation.</exception>
    public static IReadOnlyDictionary<string, TeamProfile> LoadEmbedded() => _embedded.Value;

    /// <summary>
    /// Parses and validates profiles from named JSON sources. Every failure is reported as
    /// <see cref="InvalidDataException"/> and names the source it came from.
    /// </summary>
    /// <param name="sources">Each source's name (used in error messages) and its JSON content.</param>
    /// <returns>The profiles, keyed by team ID.</returns>
    /// <exception cref="InvalidDataException">A source is malformed or invalid, or two sources share a team ID.</exception>
    internal static IReadOnlyDictionary<string, TeamProfile> Parse(IEnumerable<(string Source, Stream Json)> sources)
    {
        var profiles = new Dictionary<string, (TeamProfile Profile, string Source)>();

        foreach (var (source, json) in sources)
        {
            var profile = Deserialize(source, json);
            Validate(profile, source);

            if (profiles.TryGetValue(profile.Id, out var existing))
            {
                throw new InvalidDataException($"Team ID '{profile.Id}' is defined in both '{existing.Source}' and '{source}'.");
            }

            profiles[profile.Id] = (profile, source);
        }

        return profiles.Count > 0
            ? profiles.ToDictionary(pair => pair.Key, pair => pair.Value.Profile)
            : throw new InvalidDataException("No team profiles were found.");
    }

    private static IReadOnlyDictionary<string, TeamProfile> LoadAllEmbedded()
    {
        var streams = EmbeddedJson.Names(_resourcePrefix).Select(name => (name, EmbeddedJson.Open(name))).ToList();
        try
        {
            return Parse(streams);
        }
        finally
        {
            streams.ForEach(pair => pair.Item2.Dispose());
        }
    }

    private static TeamProfile Deserialize(string source, Stream json) => EmbeddedJson.Deserialize<TeamProfile>(source, json, "Team profile");

    private static void Validate(TeamProfile profile, string source)
    {
        var problems = new List<string>();

        // JSON can leave non-nullable strings null, so check them here, where the source is known.
        RequireText(profile.Id, "the team ID", problems);
        RequireText(profile.Name, "the team name", problems);
        RequireText(profile.ShortName, "the short name", problems);
        RequireText(profile.Venue, "the venue", problems);

        // "required" only checks a property is present; an explicit null still gets through.
        if (profile.Squad is null || profile.Tendencies is null || profile.Colours is null)
        {
            throw new InvalidDataException($"Team profile '{source}' is invalid: squad, tendencies and colours must all be present.");
        }

        foreach (var (player, index) in profile.Squad.Select((p, i) => (p, i)))
        {
            var label = $"squad entry {index + 1}";
            RequireText(player.Id, $"{label}'s ID", problems);
            RequireText(player.Name, $"{label}'s name", problems);
            RequireText(player.Role, $"{label}'s role", problems);
            problems.AddRange(TraitProblems(player, label));
        }

        if (profile.Squad.Count < _minimumSquadSize)
        {
            problems.Add($"the squad has {profile.Squad.Count} players; at least {_minimumSquadSize} are needed");
        }

        if (!profile.Squad.Any(p => p.Position == PlayerPosition.Goalkeeper))
        {
            problems.Add("the squad has no goalkeeper");
        }

        problems.AddRange(CatalogChecks.Duplicates(profile.Squad.Select(p => p.Id), "player ID"));
        problems.AddRange(CatalogChecks.Duplicates(profile.Squad.Select(p => p.ShirtNumber.ToString(CultureInfo.InvariantCulture)), "shirt number"));

        var tendencies = profile.Tendencies;
        foreach (var (name, value) in new[]
                 {
                     (nameof(tendencies.PressingIntensity), tendencies.PressingIntensity),
                     (nameof(tendencies.Width), tendencies.Width),
                     (nameof(tendencies.Directness), tendencies.Directness),
                     (nameof(tendencies.ShotAppetite), tendencies.ShotAppetite)
                 })
        {
            if (!Ranges.IsBetween(value, 0, 1))
            {
                problems.Add($"{name} is {value.ToString(CultureInfo.InvariantCulture)}; tendencies must be between 0 and 1");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException($"Team profile '{source}' is invalid: {string.Join("; ", problems)}.");
        }
    }

    // The traits of ADR-019. Top speeds are checked against a human range, so a value in m/s
    // (about 9) or a typo (335) is caught.
    private static IEnumerable<string> TraitProblems(Player player, string label)
    {
        if (!Enum.IsDefined(player.PreferredFoot))
        {
            yield return $"{label}'s preferred foot is missing";
        }

        if (!Ranges.IsBetween(player.TopSpeedKmh, _minimumTopSpeedKmh, _maximumTopSpeedKmh))
        {
            yield return $"{label}'s top speed is {player.TopSpeedKmh.ToString(CultureInfo.InvariantCulture)} km/h; it must be between {_minimumTopSpeedKmh} and {_maximumTopSpeedKmh}";
        }

        foreach (var (name, value) in new[] { ("finishing", player.Finishing), ("passing", player.Passing), ("discipline", player.Discipline) })
        {
            if (!Ranges.IsBetween(value, 0, 1))
            {
                yield return $"{label}'s {name} is {value.ToString(CultureInfo.InvariantCulture)}; ratings must be between 0 and 1";
            }
        }

        // ADR-020: an aim or a dive bias is a set of weights, so each must be a non-negative
        // number and at least one must be above zero.
        if (player.PenaltyAim is { } aim && !AreWeights(aim.Weights))
        {
            yield return $"{label}'s penalty aim must be non-negative weights with at least one above 0";
        }

        if (player.Dive is { } dive)
        {
            if (player.Position != PlayerPosition.Goalkeeper)
            {
                yield return $"{label} has a dive bias but isn't a goalkeeper";
            }

            if (!AreWeights([dive.Left, dive.Stay, dive.Right]))
            {
                yield return $"{label}'s dive bias must be non-negative weights with at least one above 0";
            }
        }
    }

    private static bool AreWeights(IReadOnlyList<double> weights) =>
        weights.All(w => Ranges.IsBetween(w, 0, double.MaxValue)) && weights.Sum() > 0;

    private static void RequireText(string? value, string label, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"{label} is missing");
        }
    }
}
