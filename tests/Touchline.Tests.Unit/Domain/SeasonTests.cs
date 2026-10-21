extern alias simulator;
using FluentAssertions;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain.Matches;
using Touchline.Domain.Season;
using Touchline.Domain.Teams;

namespace Touchline.Tests.Unit.Domain;

/// <summary>ADR-025's season model: full time, a player's fixture, and the rebuilt season with its bans.</summary>
[TestFixture]
public class SeasonTests
{
    private const string _season = "2026-27";

    private static readonly IReadOnlyDictionary<string, TeamProfile> _teams = TeamProfileCatalog.LoadEmbedded();

    internal static List<MatchEvent> Fixture(int seed, int round = 1) =>
        [.. new MatchSimulator(_teams["north-bank-fc"], _teams["thames-valley-united"], seed,
            new MatchOptions { Kind = MatchKind.Fixture, Season = _season, Round = round }).Play()];

    // Full time

    [Test]
    public void A_match_is_at_full_time_only_once_the_second_half_ends()
    {
        var events = Fixture(42);
        var halfTime = events.FindIndex(e => e is { Type: MatchEventType.PeriodEnd, Time.Period: MatchPeriod.FirstHalf });

        var atHalfTime = events.Take(halfTime + 1).Aggregate(MatchState.KickOff(events[0].MatchId), (state, e) => state.Apply(e));
        var atTheEnd = events.Aggregate(MatchState.KickOff(events[0].MatchId), (state, e) => state.Apply(e));

        atHalfTime.FullTime.Should().BeFalse("the first half's end is half-time");
        atTheEnd.FullTime.Should().BeTrue();
    }

    // A player's fixture

    [Test]
    public void Every_starter_gets_a_document_and_together_they_add_up_to_the_match()
    {
        var events = Fixture(42);
        var metadata = events[0].Metadata!;
        var final = events.Aggregate(MatchState.KickOff(events[0].MatchId), (state, e) => state.Apply(e));

        var players = PlayerMatch.FromFixture(metadata, events);

        players.Should().HaveCount(22, "a player who never touches the ball still made an appearance");
        players.Select(p => p.Id).Should().OnlyHaveUniqueItems().And.OnlyContain(id => id.StartsWith(events[0].MatchId + ":", StringComparison.Ordinal));
        players.Should().OnlyContain(p => p.Season == _season && p.Round == 1);

        var home = players.Where(p => p.TeamId == metadata.Home.Id).ToList();
        home.Sum(p => p.Goals).Should().Be(final.Home.Goals);
        home.Sum(p => p.Shots).Should().Be(final.Home.Shots);
        home.Sum(p => p.Passes).Should().Be(final.Home.Passes);
        home.Sum(p => p.PassesCompleted).Should().Be(final.Home.PassesCompleted);
        home.Sum(p => p.Fouls).Should().Be(final.Home.Fouls);
        home.Sum(p => p.YellowCards).Should().Be(final.Home.YellowCards);
        home.Sum(p => p.ExpectedGoals).Should().BeApproximately(final.Home.ExpectedGoals, 1e-9);
        players.Sum(p => p.Assists).Should().BeLessThanOrEqualTo(final.Home.Goals + final.Away.Goals);
    }

    [Test]
    public void A_penalty_is_recorded_for_the_taker_and_the_keeper_who_faced_it()
    {
        var (events, penalty) = Enumerable.Range(1, 300)
            .Select(seed => Fixture(seed))
            .Select(match => (Events: match, Penalty: match.FirstOrDefault(e => e.Shot?.Situation == ShotSituation.Penalty)))
            .First(found => found.Penalty is not null);
        var metadata = events[0].Metadata!;
        var keeper = penalty!.Team == TeamSide.Home ? metadata.Away.StartingEleven[0] : metadata.Home.StartingEleven[0];

        var players = PlayerMatch.FromFixture(metadata, events);

        var taken = players.Single(p => p.PlayerId == penalty.PlayerId).PenaltiesTaken.Single(p => p.Sequence == penalty.Sequence);
        taken.Should().Be(new PenaltyRecord(events[0].MatchId, 1, penalty.Sequence, penalty.PlayerId!, keeper,
            penalty.Shot!.Outcome, penalty.Shot.GoalMouth, penalty.Shot.KeeperDive));
        players.Single(p => p.PlayerId == keeper).PenaltiesFaced.Should().ContainEquivalentOf(taken);
    }

    [Test]
    public void A_keeper_sent_off_before_a_penalty_is_not_recorded_as_facing_it()
    {
        var (events, penalty) = Enumerable.Range(1, 300)
            .Select(seed => Fixture(seed))
            .Select(match => (Events: match, Penalty: match.FirstOrDefault(e => e.Shot?.Situation == ShotSituation.Penalty)))
            .First(found => found.Penalty is not null);
        var metadata = events[0].Metadata!;
        var keeperSide = penalty!.Team == TeamSide.Home ? TeamSide.Away : TeamSide.Home;
        var keeper = keeperSide == TeamSide.Home ? metadata.Home.StartingEleven[0] : metadata.Away.StartingEleven[0];
        // The keeper is sent off just before the kick: often for the very foul that gave it.
        var red = penalty with { Sequence = penalty.Sequence - 1, Type = MatchEventType.Card, Card = CardColour.Red, Shot = null, PlayerId = keeper, Team = keeperSide };
        var withRed = events.Where(e => e.Sequence != red.Sequence).Append(red).ToList();

        var players = PlayerMatch.FromFixture(metadata, withRed);

        players.Single(p => p.PlayerId == penalty.PlayerId).PenaltiesTaken.Single(p => p.Sequence == penalty.Sequence)
            .KeeperId.Should().BeNull("whoever went in goal isn't the starter who was sent off");
        players.Single(p => p.PlayerId == keeper).PenaltiesFaced.Should().NotContain(p => p.Sequence == penalty.Sequence);
    }

    [Test]
    public void Only_a_fixture_counts_towards_a_season()
    {
        var exhibition = new MatchSimulator(_teams["north-bank-fc"], _teams["thames-valley-united"], 42).Play().ToList();

        var build = () => PlayerMatch.FromFixture(exhibition[0].Metadata!, exhibition);

        build.Should().Throw<ArgumentException>().WithMessage("*fixture*");
    }

    // The season and its bans

    [Test]
    public void Every_fifth_yellow_card_bans_the_player_for_the_next_round()
    {
        var season = PlayerSeason.Rebuild([.. Enumerable.Range(1, 10).Select(round => Played(round, yellows: 1))]);

        season.YellowCards.Should().Be(10);
        season.BannedRounds.Should().Equal(6, 11);
    }

    [Test]
    public void A_red_card_bans_the_player_for_the_next_round()
    {
        var season = PlayerSeason.Rebuild([Played(1), Played(2, reds: 1), Played(4)]);

        season.BannedRounds.Should().Equal(3);
        season.IsBannedFor(3).Should().BeTrue();
        season.IsBannedFor(4).Should().BeFalse();
    }

    [Test]
    public void Bans_earned_together_are_served_one_round_after_another()
    {
        var season = PlayerSeason.Rebuild([Played(1, yellows: 4), Played(2, yellows: 1, reds: 1)]);

        season.BannedRounds.Should().Equal(3, 4);
    }

    [Test]
    public void The_season_is_the_same_whatever_order_its_fixtures_arrive_in()
    {
        List<PlayerMatch> fixtures = [Played(3, yellows: 2, goals: 1), Played(1, yellows: 2), Played(2, yellows: 1, reds: 1, goals: 2)];

        var inOrder = PlayerSeason.Rebuild(fixtures.OrderBy(f => f.Round));
        var shuffled = PlayerSeason.Rebuild(fixtures);

        shuffled.Should().BeEquivalentTo(inOrder);
        shuffled.Should().BeEquivalentTo(new
        {
            Id = $"{_season}:nbfc-04",
            Appearances = 3,
            Goals = 3,
            YellowCards = 5,
            RedCards = 1,
            BannedRounds = new[] { 3, 4 }
        });
    }

    [Test]
    public void A_round_counts_once_even_if_two_fixtures_exist_for_it()
    {
        var first = Played(2, yellows: 3) with { Id = "a:nbfc-04", MatchId = "a" };
        var second = Played(2, yellows: 3) with { Id = "b:nbfc-04", MatchId = "b" };

        var season = PlayerSeason.Rebuild([Played(1, yellows: 1), second, first]);

        season.Appearances.Should().Be(2, "the simulator refuses a second fixture for a round, and this is the defence behind it");
        season.Rounds.Should().Equal(1, 2);
        season.YellowCards.Should().Be(4, "only the first fixture by match ID counts for round 2");
        season.BannedRounds.Should().BeEmpty();
    }

    [Test]
    public void A_season_lists_the_rounds_the_player_started_in_order()
    {
        var season = PlayerSeason.Rebuild([Played(3), Played(1), Played(4)]);

        // A player who missed round 2 (banned or rested) has no fixture for it.
        season.Rounds.Should().Equal(1, 3, 4);
    }

    [Test]
    public void A_season_keeps_the_five_most_recent_penalties_newest_first()
    {
        var fixtures = Enumerable.Range(1, 7).Select(round => Played(round) with
        {
            PenaltiesTaken = [new PenaltyRecord($"m{round}", round, 100, "nbfc-04", "tvu-01", ShotOutcome.Goal, null, null)]
        });

        var season = PlayerSeason.Rebuild(fixtures);

        season.RecentPenaltiesTaken.Select(p => p.Round).Should().Equal(7, 6, 5, 4, 3);
    }

    [Test]
    public void A_season_belongs_to_one_player()
    {
        var mixed = () => PlayerSeason.Rebuild([Played(1), Played(2) with { PlayerId = "tvu-09" }]);
        var empty = () => PlayerSeason.Rebuild([]);

        mixed.Should().Throw<ArgumentException>();
        empty.Should().Throw<ArgumentException>();
    }

    private static PlayerMatch Played(int round, int yellows = 0, int reds = 0, int goals = 0) => new(
        PlayerMatch.IdFor($"m{round}", "nbfc-04"), "nbfc-04", $"m{round}", "north-bank-fc", _season, round,
        goals, 0, goals, goals, 0.3 * goals, 30, 25, 1, yellows, reds, [], []);
}
