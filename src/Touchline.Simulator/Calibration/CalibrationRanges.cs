using Touchline.Domain.Matches;

namespace Touchline.Simulator.Calibration;

/// <summary>A per-match average that must land between two bounds.</summary>
/// <param name="Label">What it measures, in words, for messages and the calibration report.</param>
/// <param name="Measure">Reads the average from a set of matches.</param>
/// <param name="Min">The lowest realistic value, inclusive.</param>
/// <param name="Max">The highest realistic value, inclusive.</param>
public sealed record CalibrationRange(string Label, Func<SeasonAverages, double> Measure, double Min, double Max);

/// <summary>Two averages that must agree within a tolerance, such as xG and goals.</summary>
/// <param name="Label">What the pair checks, in words.</param>
/// <param name="Measure">The average under test.</param>
/// <param name="Target">The average it should track.</param>
/// <param name="Tolerance">How far apart they may be.</param>
public sealed record CalibrationPair(string Label, Func<SeasonAverages, double> Measure, Func<SeasonAverages, double> Target, double Tolerance);

/// <summary>
/// The ranges a match engine must stay inside to look like a top-flight league (agreed for slice 03,
/// extended by ADR-019, ADR-020 and ADR-022). The calibration tests and the dataset report (ADR-063)
/// both read these, so the two can't drift apart.
/// </summary>
public static class CalibrationRanges
{
    /// <summary>The per-match ranges.</summary>
    public static IReadOnlyList<CalibrationRange> Ranges { get; } =
    [
        new("goals per match", a => a.Goals, 2.2, 3.4),
        new("shots per match", a => a.Shots, 20, 30),
        new("share of shots on target", a => a.OnTargetShare, 0.28, 0.40),
        new("passes per match", a => a.Passes, 750, 1050),
        new("pass completion", a => a.PassCompletion, 0.77, 0.87),
        new("fouls per match", a => a.Fouls, 16, 26),
        new("yellow cards per match", a => a.YellowCards, 2.5, 5),
        new("penalties per match", a => a.Penalties, 0.1, 0.5),
        new("red cards per match, including second yellows", a => a.RedCards, 0.05, 0.25),
        new("weak-foot shots as a share of footed shots", a => a.WeakFootShareOfFootedShots, 0.15, 0.30),
        new("headers as a share of all shots", a => a.HeaderShare, 0.10, 0.20),
        new("shots hitting the post or bar per match", a => a.WoodworkHits, 0.5, 1.3),
        new("advantages played per match", a => a.Advantages, 2, 6),
        new("throw-ins per match", a => a.ThrowIns, 35, 50),
        new("goal kicks per match", a => a.GoalKicks, 12, 20),
        new("corners per match", a => a.Corners, 8, 12),
        new("shots cleared off the line per match", a => a.ClearedOffLine, 0.1, 0.4),
    ];

    /// <summary>The pairs that must agree.</summary>
    public static IReadOnlyList<CalibrationPair> Pairs { get; } =
    [
        new("total xG should track goals", a => a.ExpectedGoals, a => a.Goals, 0.4),
        new("xPass should track pass completion", a => a.MeanExpectedCompletion, a => a.PassCompletion, 0.02),
    ];

    /// <summary>
    /// How many standard errors a season's average may sit outside its range and still count as
    /// sampling noise. The ranges were agreed on 200 matches; a season of 38 matches is a smaller
    /// sample, so a rare event can land just outside a range by chance.
    /// </summary>
    public const double SamplingAllowance = 3;

    /// <summary>
    /// Checks a set of matches against every range and pair. Each result says whether the season
    /// is strictly inside the range, and whether it is at least inside the range once the sampling
    /// allowance is added (the standard error is taken across the matches, one match at a time).
    /// </summary>
    /// <param name="matches">The matches, each as its full list of events. At least two.</param>
    /// <returns>One result per range, then one per pair.</returns>
    public static IReadOnlyList<CalibrationResult> Evaluate(IReadOnlyList<IReadOnlyList<MatchEvent>> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (matches.Count < 2)
        {
            throw new ArgumentException("A standard error needs at least two matches.", nameof(matches));
        }

        var season = SeasonAverages.From(matches);
        var perMatch = matches.Select(match => SeasonAverages.From([match])).ToList();
        var results = new List<CalibrationResult>();

        foreach (var range in Ranges)
        {
            var value = range.Measure(season);
            var error = StandardError(perMatch.Select(range.Measure));
            var inside = value >= range.Min && value <= range.Max;
            var near = value >= range.Min - SamplingAllowance * error && value <= range.Max + SamplingAllowance * error;
            results.Add(new CalibrationResult(range.Label, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{range.Min:0.##} to {range.Max:0.##}"), value, error, inside, near));
        }

        foreach (var pair in Pairs)
        {
            var value = pair.Measure(season);
            var target = pair.Target(season);
            var error = StandardError(perMatch.Select(a => pair.Measure(a) - pair.Target(a)));
            var gap = Math.Abs(value - target);
            results.Add(new CalibrationResult(pair.Label, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"within {pair.Tolerance:0.##} of {target:0.00}"), value, error, gap <= pair.Tolerance, gap <= pair.Tolerance + SamplingAllowance * error));
        }

        return results;
    }

    // The standard error of the mean, from the sample standard deviation. Matches with a NaN
    // (a measure with nothing to divide by) are left out.
    private static double StandardError(IEnumerable<double> values)
    {
        var sample = values.Where(double.IsFinite).ToList();
        if (sample.Count < 2)
        {
            return 0;
        }

        var mean = sample.Average();
        var variance = sample.Sum(x => (x - mean) * (x - mean)) / (sample.Count - 1);
        return Math.Sqrt(variance / sample.Count);
    }
}

/// <summary>One range or pair checked against a set of matches.</summary>
/// <param name="Label">What was checked.</param>
/// <param name="Accepted">The bounds, in words.</param>
/// <param name="Value">The season's average.</param>
/// <param name="StandardError">The standard error of that average across the matches.</param>
/// <param name="InRange">True when the average is inside the bounds as they stand.</param>
/// <param name="WithinSampling">True when the average is inside the bounds once <see cref="CalibrationRanges.SamplingAllowance"/> standard errors are allowed.</param>
public sealed record CalibrationResult(string Label, string Accepted, double Value, double StandardError, bool InRange, bool WithinSampling);
