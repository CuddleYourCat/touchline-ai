extern alias simulator;
using FluentAssertions;
using FluentAssertions.Execution;
using simulator::Touchline.Simulator.Calibration;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain.Matches;
using Touchline.Domain.Teams;

namespace Touchline.Tests.Unit.Simulator;

/// <summary>
/// Plays 200 seeded matches and checks the per-match averages land inside realistic ranges
/// for a top-flight league (agreed for slice 03, extended by ADR-019). Seeds are fixed, so
/// the result is exact and repeatable: a failure here means a tuning change moved the game,
/// never flakiness. The averages and ranges are shared with the dataset export (ADR-063).
/// </summary>
[TestFixture]
public class CalibrationTests
{
    private const int _matches = 200;

    private SeasonAverages _averages = null!;

    [OneTimeSetUp]
    public void PlayTheSeason()
    {
        var teams = TeamProfileCatalog.LoadEmbedded();
        var games = Enumerable.Range(1000, _matches)
            .Select(seed => (IReadOnlyList<MatchEvent>)new MatchSimulator(teams["north-bank-fc"], teams["thames-valley-united"], seed).Play().ToList())
            .ToList();

        _averages = SeasonAverages.From(games);
    }

    [Test]
    public void Averages_land_inside_the_agreed_ranges()
    {
        // Printed so a tuning run shows every number, not just the ones out of range.
        TestContext.Out.WriteLine(_averages);
        using var scope = new AssertionScope();

        foreach (var range in CalibrationRanges.Ranges)
        {
            range.Measure(_averages).Should().BeInRange(range.Min, range.Max, range.Label);
        }

        foreach (var pair in CalibrationRanges.Pairs)
        {
            pair.Measure(_averages).Should().BeApproximately(pair.Target(_averages), pair.Tolerance, pair.Label);
        }
    }
}
