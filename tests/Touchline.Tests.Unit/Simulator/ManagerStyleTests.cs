extern alias simulator;
using FluentAssertions;
using FluentAssertions.Execution;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain.Matches;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;

namespace Touchline.Tests.Unit.Simulator;

/// <summary>
/// A manager's style changes how the team plays (ADR-050). The same fan squad plays the same
/// seeds under each style, so only the tendencies differ. Each check reads the event the engine
/// drives from that tendency: pressing sets how often the defending side presses, directness
/// how often a pass goes long, width how far a pass travels across the pitch, and shot
/// appetite how readily the team shoots.
/// </summary>
[TestFixture]
public class ManagerStyleTests
{
    private const int _matches = 150;

    // A long ball travels 18-38 m forward and a short pass at most 14 m, so 16 m tells them apart.
    private const double _longPassMetres = 16;

    private Dictionary<ManagerStyle, StyleStats> _stats = null!;

    [OneTimeSetUp]
    public void PlayEachStyle()
    {
        var opponent = TeamProfileCatalog.LoadEmbedded()["north-bank-fc"];
        _stats = Enum.GetValues<ManagerStyle>().ToDictionary(style => style, style =>
        {
            var team = FanTeamProfiles.Build(TestFanTeams.Standard("fan-style", style));
            var events = Enumerable.Range(4000, _matches).SelectMany(seed => new MatchSimulator(team, opponent, seed).Play()).ToList();
            var stats = StyleStats.From(events);
            TestContext.Progress.WriteLine($"{style}: {stats}");
            return stats;
        });
    }

    [Test]
    public void A_high_press_presses_more_than_a_counter()
    {
        _stats[ManagerStyle.HighPress].PressureShare.Should().BeGreaterThan(_stats[ManagerStyle.Counter].PressureShare * 1.2);
    }

    [Test]
    public void Direct_and_counter_sides_go_long_more_than_a_possession_side()
    {
        using var scope = new AssertionScope();
        _stats[ManagerStyle.Direct].LongPassShare.Should().BeGreaterThan(_stats[ManagerStyle.Possession].LongPassShare * 1.1);
        _stats[ManagerStyle.Counter].LongPassShare.Should().BeGreaterThan(_stats[ManagerStyle.Possession].LongPassShare * 1.1);
    }

    [Test]
    public void A_high_press_plays_wider_than_a_counter()
    {
        _stats[ManagerStyle.HighPress].MeanSidewaysMetres.Should().BeGreaterThan(_stats[ManagerStyle.Counter].MeanSidewaysMetres);
    }

    // Direct and Counter share their width and directness, so this is the closest the tables
    // come to isolating shot appetite (0.55 against 0.50). The effect is small: about 6%.
    [Test]
    public void A_direct_side_shoots_more_readily_than_a_counter_side()
    {
        _stats[ManagerStyle.Direct].ShotsPerAction.Should().BeGreaterThan(_stats[ManagerStyle.Counter].ShotsPerAction);
    }

    private sealed record StyleStats(double PressureShare, double LongPassShare, double MeanSidewaysMetres, double ShotsPerAction)
    {
        // The fan team is always at home.
        public static StyleStats From(IReadOnlyList<MatchEvent> events)
        {
            static bool IsAction(MatchEvent e) => e.Type is MatchEventType.Pass or MatchEventType.Carry or MatchEventType.Shot;
            var passes = events.Where(e => e is { Type: MatchEventType.Pass, Team: TeamSide.Home, End: not null }).ToList();
            var homeActions = events.Count(e => e.Team == TeamSide.Home && IsAction(e));

            return new StyleStats(
                PressureShare: events.Count(e => e is { Type: MatchEventType.Pressure, Team: TeamSide.Home }) / (double)events.Count(e => e.Team == TeamSide.Away && IsAction(e)),
                LongPassShare: passes.Count(p => p.End!.Value.X - p.Start.X >= _longPassMetres) / (double)passes.Count,
                MeanSidewaysMetres: passes.Average(p => Math.Abs(p.End!.Value.Y - p.Start.Y)),
                ShotsPerAction: events.Count(e => e is { Type: MatchEventType.Shot, Team: TeamSide.Home }) / (double)homeActions);
        }

        public override string ToString() =>
            $"pressure {PressureShare:F3}, long passes {LongPassShare:F3}, sideways {MeanSidewaysMetres:F2} m, shots per action {ShotsPerAction:F4}";
    }
}
