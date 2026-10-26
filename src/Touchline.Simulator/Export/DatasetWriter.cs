using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Teams;
using Touchline.Simulator.Calibration;

namespace Touchline.Simulator.Export;

/// <summary>One team's totals over a match: what it did, and what it was awarded.</summary>
/// <param name="Shots">Shots taken.</param>
/// <param name="ShotsOnTarget">Shots that were a goal or a save.</param>
/// <param name="Goals">Shots that went in.</param>
/// <param name="ExpectedGoals">The sum of the shots' xG, to three places.</param>
/// <param name="Penalties">Penalties taken.</param>
/// <param name="Passes">Passes attempted.</param>
/// <param name="PassesCompleted">Passes that found a team-mate.</param>
/// <param name="Carries">Carries with the ball.</param>
/// <param name="Fouls">Fouls committed.</param>
/// <param name="YellowCards">Yellow cards shown to its players.</param>
/// <param name="RedCards">Red cards shown to its players, second yellows included.</param>
/// <param name="Corners">Corners taken, which the side awarded to it by the other side's last touch.</param>
/// <param name="ThrowIns">Throw-ins taken.</param>
/// <param name="GoalKicks">Goal kicks taken.</param>
public sealed record TeamTotals(
    int Shots,
    int ShotsOnTarget,
    int Goals,
    double ExpectedGoals,
    int Penalties,
    int Passes,
    int PassesCompleted,
    int Carries,
    int Fouls,
    int YellowCards,
    int RedCards,
    int Corners,
    int ThrowIns,
    int GoalKicks);

/// <summary>A final score.</summary>
/// <param name="Home">The home side's goals.</param>
/// <param name="Away">The away side's goals.</param>
public sealed record ScoreLine(int Home, int Away);

/// <summary>One line of <c>matches.jsonl</c>: a match's metadata, line-ups, final score and totals (ADR-063).</summary>
/// <param name="MatchId">The match ID, as on every event.</param>
/// <param name="Seed">The seed the match was played with.</param>
/// <param name="Season">The season label.</param>
/// <param name="Round">The round, from 1.</param>
/// <param name="Kind">Whether it counts towards the season.</param>
/// <param name="KickOffUtc">The scheduled kick-off, in UTC.</param>
/// <param name="Venue">The home club's fictional ground.</param>
/// <param name="Referee">The referee.</param>
/// <param name="Home">The home side's line-up, goalkeeper first.</param>
/// <param name="Away">The away side's line-up, goalkeeper first.</param>
/// <param name="FinalScore">The final score.</param>
/// <param name="HomeTotals">The home side's totals.</param>
/// <param name="AwayTotals">The away side's totals.</param>
/// <param name="Synthetic">Always true: the match is simulated, and every club and player is fictional.</param>
public sealed record MatchLine(
    string MatchId,
    int Seed,
    string Season,
    int Round,
    MatchKind Kind,
    DateTimeOffset KickOffUtc,
    string Venue,
    RefereeSheet Referee,
    TeamSheet Home,
    TeamSheet Away,
    ScoreLine FinalScore,
    TeamTotals HomeTotals,
    TeamTotals AwayTotals,
    bool Synthetic);

/// <summary>
/// Writes the synthetic season to a folder (ADR-063): the events, one per line and gzipped, the
/// matches, the JSON Schema for both, the calibration report and a README. Everything is derived
/// from the seeds, in invariant culture, so the same code writes the same bytes.
/// </summary>
public static class DatasetWriter
{
    /// <summary>The folder written when none is given, inside the repository root.</summary>
    public const string DefaultFolder = "dataset";

    /// <summary>Gzips every event of every match, one JSON object per line, in the same JSON as Event Hubs (ADR-063).</summary>
    /// <param name="matches">The matches, in seed order.</param>
    /// <returns>The gzip file. The header's modification time is zero, so the bytes depend only on the events.</returns>
    public static byte[] EventsGzip(IEnumerable<SeasonMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            foreach (var match in matches)
            {
                foreach (var matchEvent in match.Events)
                {
                    gzip.Write(JsonSerializer.SerializeToUtf8Bytes(matchEvent, DomainJson.Options));
                    gzip.WriteByte((byte)'\n');
                }
            }
        }

        // The gzip header's OS byte (the tenth) is written by the platform's zlib, so it would differ
        // between Windows and Linux. 255 means "unknown", so the file is the same wherever it's written.
        var bytes = buffer.ToArray();
        bytes[9] = _gzipUnknownOs;
        return bytes;
    }

    private const byte _gzipUnknownOs = 255;

    /// <summary>Writes one match line per match (ADR-063).</summary>
    /// <param name="matches">The matches, in seed order.</param>
    /// <returns>The JSON lines, as UTF-8 with LF endings.</returns>
    public static byte[] MatchesJsonl(IEnumerable<SeasonMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        using var buffer = new MemoryStream();
        foreach (var match in matches)
        {
            buffer.Write(JsonSerializer.SerializeToUtf8Bytes(ToMatchLine(match), DomainJson.Options));
            buffer.WriteByte((byte)'\n');
        }

        return buffer.ToArray();
    }

    /// <summary>The match line for one match: its metadata, line-ups, score and totals.</summary>
    /// <param name="match">The match.</param>
    /// <returns>The line.</returns>
    public static MatchLine ToMatchLine(SeasonMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        var metadata = match.Metadata;
        var home = TotalsFor(match.Events, TeamSide.Home);
        var away = TotalsFor(match.Events, TeamSide.Away);

        return new MatchLine(
            MatchId: match.MatchId,
            Seed: match.Seed,
            Season: metadata.Season ?? SeasonDataset.Season,
            Round: metadata.Round ?? match.Seed,
            Kind: metadata.Kind,
            KickOffUtc: metadata.KickOffUtc,
            Venue: metadata.Venue,
            Referee: metadata.Referee,
            Home: metadata.Home,
            Away: metadata.Away,
            FinalScore: new ScoreLine(home.Goals, away.Goals),
            HomeTotals: home,
            AwayTotals: away,
            Synthetic: true);
    }

    /// <summary>The totals for one side of a match.</summary>
    /// <param name="events">Every event of the match.</param>
    /// <param name="side">The side.</param>
    /// <returns>The totals.</returns>
    public static TeamTotals TotalsFor(IReadOnlyList<MatchEvent> events, TeamSide side)
    {
        var own = events.Where(e => e.Team == side).ToList();
        var shots = own.Where(e => e.Type == MatchEventType.Shot).ToList();
        var passes = own.Where(e => e.Type == MatchEventType.Pass).ToList();

        // A restart is taken by the side that didn't touch the ball last (ADR-022).
        var restarts = events.Where(e => e.Type == MatchEventType.BallOutOfPlay && e.Team != side).ToList();

        return new TeamTotals(
            Shots: shots.Count,
            ShotsOnTarget: shots.Count(s => s.Shot!.OnTarget),
            Goals: shots.Count(s => s.IsGoal),
            ExpectedGoals: Math.Round(shots.Sum(s => s.Shot!.ExpectedGoals), 3, MidpointRounding.ToEven),
            Penalties: shots.Count(s => s.Shot!.Situation == ShotSituation.Penalty),
            Passes: passes.Count,
            PassesCompleted: passes.Count(p => p.Successful),
            Carries: own.Count(e => e.Type == MatchEventType.Carry),
            Fouls: own.Count(e => e.Type == MatchEventType.Foul),
            YellowCards: own.Count(e => e.Card == CardColour.Yellow),
            RedCards: own.Count(e => e.Card == CardColour.Red),
            Corners: restarts.Count(e => e.Restart == RestartKind.Corner),
            ThrowIns: restarts.Count(e => e.Restart == RestartKind.ThrowIn),
            GoalKicks: restarts.Count(e => e.Restart == RestartKind.GoalKick));
    }

    /// <summary>Writes the calibration report (ADR-063): the season's averages against the shared ranges.</summary>
    /// <param name="results">The checks, from <see cref="CalibrationRanges.Evaluate"/>.</param>
    /// <param name="matchCount">How many matches the season has.</param>
    /// <returns>The Markdown, with LF endings.</returns>
    public static string CalibrationReport(IReadOnlyList<CalibrationResult> results, int matchCount)
    {
        ArgumentNullException.ThrowIfNull(results);
        var text = new StringBuilder();
        text.Append("# Calibration of the synthetic season\n\n");
        text.Append("> Synthetic data. Every club and player is fictional, and every match is simulated from its seed.\n\n");
        text.Append(CultureInfo.InvariantCulture, $"The season is {matchCount} matches between two fictional clubs, seeds {SeasonDataset.Seeds[0]} to {SeasonDataset.Seeds[^1]}, home and away alternating. The figures are per match, averaged over the season, and checked against the same ranges as `CalibrationTests`.\n\n");
        text.Append(CultureInfo.InvariantCulture, $"A season this size is a smaller sample than the 200 matches the ranges were agreed on. So a result is read two ways: **in range** when it sits inside the accepted bounds, and **within sampling error** when it sits outside them by no more than {CalibrationRanges.SamplingAllowance} standard errors. Anything else is **out of range**.\n\n");
        text.Append("| Measure | Season | Accepted | Standard error | Result |\n");
        text.Append("|---|---:|---|---:|---|\n");

        foreach (var result in results)
        {
            text.Append(CultureInfo.InvariantCulture, $"| {result.Label} | {result.Value:0.00} | {result.Accepted} | {result.StandardError:0.000} | {Verdict(result)} |\n");
        }

        text.Append("\nThe numbers come from `SeasonAverages` and `CalibrationRanges`, the same code the calibration tests use. Regenerate with the export command in the README.\n");
        return text.ToString();
    }

    /// <summary>The README for the dataset folder (ADR-063).</summary>
    public const string Readme = """
        # Touchline synthetic season

        > **Synthetic data.** Every match is simulated from a seed by the touchline-ai match engine. Every club, player, referee and ground is fictional, and no real match, club or player data is used.

        This folder is a 38-match season between two fictional clubs, North Bank FC and Thames Valley United. The clubs play each other once in every round, home and away alternating. Round N is seed N, so each match can be rebuilt from its seed alone (ADR-063).

        ## Files

        | File | What it is |
        |---|---|
        | `events.jsonl.gz` | Every event, one JSON object per line, gzipped. The JSON is the same as the events sent to Event Hubs (`DomainJson`). It includes `Substitution` events (ADR-064). |
        | `matches.jsonl` | One line per match: the metadata, both starting elevens and benches, the final score and each side's totals. `synthetic` is always `true`. |
        | `schema.json` | A JSON Schema (draft 2020-12) for both line types, under `$defs/event` and `$defs/match`. |
        | `CALIBRATION.md` | The season's averages against the ranges the simulator is tuned to (`CalibrationTests`). |
        | `README.md` | This file. |

        Each match ID includes its season and round, for example `nbk-tvu-2026-27-r1-1` for seed 1 (ADR-020, ADR-025).

        ## Regenerate

        From any folder, with the .NET 10 SDK installed and no Azure resources needed. A relative folder is taken from the repository root:

        ```
        dotnet run --project src/Touchline.Simulator -- export --out dataset
        ```

        The export is deterministic. Running it again writes the same bytes to the same files, on the same machine and .NET runtime. The gzip header's modification time is zero and its OS byte is fixed, and the data has no wall-clock times: kick-offs are a week apart from a fixed first date. Byte-identical output across different machines also depends on the runtime's zlib build, which the tests don't cover.

        ## Checks

        The unit tests in `tests/Touchline.Tests.Unit/Simulator/DatasetExportTests.cs` export two matches twice and compare the bytes, check that every exported line matches `schema.json`, and check that the season's averages land in the calibration ranges, allowing for sampling error.

        A 38-match season is a smaller sample than the 200 matches the ranges were agreed on, so `CALIBRATION.md` reads each average two ways: inside the accepted bounds, or within three standard errors of them. The tests use the same reading, and the strict 200-match check stays in `CalibrationTests`.

        ## Known limits

        - The export has no season store, so no player is suspended from one match to the next. Every match starts with its full squads.
        - The line-ups are the starting elevens and benches the engine picks by default. A `Substitution` event names the player going off (`playerId`) and the one coming on (`replacementPlayerId`), as ADR-064 describes. The team totals don't count substitutions.
        - The schema check in the tests is a small subset of JSON Schema (types, required properties, enums, `$ref`, `items` and `additionalProperties`), not a full validator.

        """;

    /// <summary>The JSON Schema for both line types (ADR-063). Generated text, kept in <see cref="DatasetSchema"/>.</summary>
    public static string Schema => DatasetSchema.Json;

    /// <summary>
    /// Writes the dataset. Run as <c>export --out &lt;folder&gt;</c>. A relative folder is taken from the
    /// repository root, not the working directory, because <c>dotnet run --project</c> always runs the
    /// app from the project's folder. The folder defaults to <see cref="DefaultFolder"/>.
    /// </summary>
    /// <param name="args">The arguments after <c>export</c>.</param>
    /// <param name="output">Where the progress lines go.</param>
    /// <param name="cancellationToken">Stops the export.</param>
    /// <returns>A task that completes once every file is written.</returns>
    /// <exception cref="ArgumentException">An argument isn't recognised.</exception>
    public static async Task RunAsync(IReadOnlyList<string> args, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        var folder = Path.GetFullPath(ParseFolder(args), RepositoryRoot());

        var teams = TeamProfileCatalog.LoadEmbedded();
        var matches = SeasonDataset.Play(teams);
        var results = CalibrationRanges.Evaluate(matches.Select(m => (IReadOnlyList<MatchEvent>)m.Events).ToList());

        Directory.CreateDirectory(folder);
        await WriteAsync(folder, "events.jsonl.gz", EventsGzip(matches), cancellationToken);
        await WriteAsync(folder, "matches.jsonl", MatchesJsonl(matches), cancellationToken);
        await WriteAsync(folder, "schema.json", Encoding.UTF8.GetBytes(Schema), cancellationToken);
        await WriteAsync(folder, "CALIBRATION.md", Encoding.UTF8.GetBytes(CalibrationReport(results, matches.Count)), cancellationToken);
        await WriteAsync(folder, "README.md", Encoding.UTF8.GetBytes(Readme), cancellationToken);

        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"Wrote {matches.Count} matches to {folder}."));
    }

    // The folder holding Touchline.slnx, found by walking up from the app's own folder. Falls back to
    // the working directory where the solution isn't above the app (a published copy, say).
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Touchline.slnx")))
            {
                return directory.FullName;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private static string ParseFolder(IReadOnlyList<string> args)
    {
        var folder = DefaultFolder;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] != "--out")
            {
                throw new ArgumentException($"Unknown argument '{args[i]}'. Usage: export [--out <folder>] (the folder defaults to '{DefaultFolder}').", nameof(args));
            }

            if (i + 1 >= args.Count)
            {
                throw new ArgumentException("The --out option needs a folder, for example: export --out dataset.", nameof(args));
            }

            folder = args[++i];
        }

        return folder;
    }

    private static async Task WriteAsync(string folder, string name, byte[] bytes, CancellationToken cancellationToken) =>
        await File.WriteAllBytesAsync(Path.Combine(folder, name), bytes, cancellationToken);

    private static string Verdict(CalibrationResult result) =>
        result.InRange ? "in range"
        : result.WithinSampling ? "within sampling error"
        : "**out of range**";
}
