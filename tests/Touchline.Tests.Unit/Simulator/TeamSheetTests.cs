extern alias simulator;
using FluentAssertions;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;

namespace Touchline.Tests.Unit.Simulator;

/// <summary>The engine plays a chosen eleven (ADR-032, ADR-050).</summary>
[TestFixture]
public class TeamSheetTests
{
    private static readonly IReadOnlyDictionary<string, TeamProfile> _teams = TeamProfileCatalog.LoadEmbedded();
    private static TeamProfile NorthBank => _teams["north-bank-fc"];
    private static TeamProfile ThamesValley => _teams["thames-valley-united"];

    // A 5-4-1 from the bench and the first team, listed out of line order on purpose.
    private static readonly MatchdaySheet _fiveFourOne = new(
        ["nbfc-19", "nbfc-16", "nbfc-02", "nbfc-13", "nbfc-03", "nbfc-04", "nbfc-05", "nbfc-06", "nbfc-08", "nbfc-15", "nbfc-10"],
        ["nbfc-01", "nbfc-07"]);

    private static List<MatchEvent> Play(int seed, MatchdaySheet? home = null, MatchdaySheet? away = null, IReadOnlySet<string>? unavailable = null) =>
        new MatchSimulator(NorthBank, ThamesValley, seed, new MatchOptions
        {
            HomeSheet = home,
            AwaySheet = away,
            Unavailable = unavailable ?? new HashSet<string>()
        }).Play().ToList();

    [Test]
    public void The_metadata_names_the_chosen_eleven_in_line_order_with_the_keeper_first()
    {
        var metadata = Play(seed: 3, home: _fiveFourOne)[0].Metadata!;

        metadata.Home.StartingEleven.Should().Equal(
            "nbfc-13", "nbfc-02", "nbfc-03", "nbfc-04", "nbfc-06", "nbfc-15", "nbfc-16", "nbfc-05", "nbfc-08", "nbfc-10", "nbfc-19");
        MatchdaySheetRules.Formation(metadata.Home.StartingEleven.Select(id => NorthBank.FindPlayer(id)!.Position)).Should().Be("5-4-1");
        metadata.Away.StartingEleven.Should().Equal(new MatchSimulator(NorthBank, ThamesValley, 3).Play().First().Metadata!.Away.StartingEleven,
            "the side without a sheet keeps its default pick");
    }

    [Test]
    public void Only_the_chosen_eleven_play()
    {
        var match = Play(seed: 11, home: _fiveFourOne);

        var homePlayers = match.Where(e => e.Team == TeamSide.Home && e.PlayerId is not null).Select(e => e.PlayerId!).ToHashSet();

        homePlayers.Should().BeSubsetOf(_fiveFourOne.StartingEleven);
        homePlayers.Should().NotContain(_fiveFourOne.Substitutes, "the bench is stored but not played");
    }

    [Test]
    public void A_sheet_replays_exactly()
    {
        Play(seed: 5, home: _fiveFourOne).Should().BeEquivalentTo(Play(seed: 5, home: _fiveFourOne), options => options.WithStrictOrdering());
    }

    [Test]
    public void A_sheet_changes_the_match()
    {
        Play(seed: 5, home: _fiveFourOne).Select(e => e.PlayerId).Should().NotEqual(Play(seed: 5).Select(e => e.PlayerId));
    }

    [Test]
    public void Corners_are_taken_by_the_first_midfielder_on_the_sheet()
    {
        var corners = Enumerable.Range(20, 10)
            .SelectMany(seed => Play(seed, home: _fiveFourOne))
            .Where(e => e is { Type: MatchEventType.Pass, Team: TeamSide.Home } && IsCornerFlag(e.Start))
            .ToList();

        corners.Should().NotBeEmpty();
        corners.Should().OnlyContain(e => e.PlayerId == "nbfc-16", "nbfc-16 is the first midfielder listed");
    }

    [Test]
    public void An_eleven_with_no_forwards_plays_a_full_match()
    {
        var (bus, sheet) = TestFanTeams.ParkTheBus();

        var match = new MatchSimulator(bus, NorthBank, 8, new MatchOptions { HomeSheet = sheet }).Play().ToList();

        match[^1].Type.Should().Be(MatchEventType.PeriodEnd);
        match[^1].Time.Period.Should().Be(MatchPeriod.SecondHalf);
        match.Aggregate(MatchState.KickOff(match[0].MatchId), (s, e) => s.Apply(e)).LastSequence.Should().Be(match.Count);
    }

    [Test]
    public void A_sheet_of_ten_is_refused()
    {
        var sheet = _fiveFourOne with { StartingEleven = [.. _fiveFourOne.StartingEleven.Skip(1)] };

        var start = () => Play(seed: 1, home: sheet);

        start.Should().Throw<ArgumentException>().WithMessage("*North Bank FC*10 players*").And.ParamName.Should().Be("HomeSheet");
    }

    [Test]
    public void A_sheet_with_two_goalkeepers_is_refused()
    {
        var sheet = _fiveFourOne with { StartingEleven = [.. _fiveFourOne.StartingEleven.Select(id => id == "nbfc-19" ? "nbfc-01" : id)], Substitutes = [] };

        var start = () => Play(seed: 1, away: null, home: sheet);

        start.Should().Throw<ArgumentException>().WithMessage("*exactly one goalkeeper, not 2*");
    }

    [Test]
    public void A_player_from_another_squad_is_refused()
    {
        var sheet = new MatchdaySheet([.. ThamesValley.Squad.Take(10).Select(p => p.Id), "nbfc-09"], []);

        var start = () => Play(seed: 1, away: sheet);

        start.Should().Throw<ArgumentException>().WithMessage("*Thames Valley United*'nbfc-09' isn't in the squad*").And.ParamName.Should().Be("AwaySheet");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    public void An_empty_player_id_is_refused_as_an_invalid_sheet(string? id)
    {
        var sheet = _fiveFourOne with { StartingEleven = [.. _fiveFourOne.StartingEleven.Select(p => p == "nbfc-19" ? id! : p)] };

        var start = () => Play(seed: 1, home: sheet);

        start.Should().Throw<ArgumentException>().Where(e => e.GetType() == typeof(ArgumentException))
            .WithMessage("*The sheet names an empty player.*");
    }

    [Test]
    public void An_unavailable_player_is_refused()
    {
        var start = () => Play(seed: 1, home: _fiveFourOne, unavailable: new HashSet<string> { "nbfc-08" });

        start.Should().Throw<ArgumentException>().WithMessage("*'nbfc-08' isn't available*");
    }

    private static bool IsCornerFlag(PitchPosition at) =>
        at.X is 0 or PitchDimensions.Length && at.Y is 0 or PitchDimensions.Width;
}
