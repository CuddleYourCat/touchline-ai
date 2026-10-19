extern alias simulator;
using FluentAssertions;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain.Matches;
using Touchline.Domain.Officials;
using Touchline.Domain.Pitch;
using Touchline.Domain.Teams;

namespace Touchline.Tests.Unit.Simulator;

/// <summary>
/// The engine's side of ADR-020: referees, advantage and deferred cards, temperament, pass
/// height and crosses, where shots go, woodwork and the penalty duel.
/// </summary>
[TestFixture]
public class OfficialsAndHeightTests
{
    private const int _matches = 400;

    private static readonly IReadOnlyDictionary<string, TeamProfile> _teams = TeamProfileCatalog.LoadEmbedded();
    private static TeamProfile NorthBank => _teams["north-bank-fc"];
    private static TeamProfile ThamesValley => _teams["thames-valley-united"];

    private List<List<MatchEvent>> _season = null!;
    private List<MatchEvent> _events = null!;
    private Dictionary<string, Player> _players = null!;

    [OneTimeSetUp]
    public void PlayTheSeason()
    {
        _season = Enumerable.Range(3000, _matches).Select(seed => Play(seed)).ToList();
        _events = _season.SelectMany(m => m).ToList();
        _players = _teams.Values.SelectMany(t => t.Squad).ToDictionary(p => p.Id);
    }

    private static List<MatchEvent> Play(int seed, MatchOptions? options = null) =>
        new MatchSimulator(NorthBank, ThamesValley, seed, options).Play().ToList();

    // Metadata

    [Test]
    public void A_match_is_an_exhibition_refereed_by_the_seed_s_referee_unless_told_otherwise()
    {
        var metadata = Play(11)[0].Metadata!;

        metadata.Kind.Should().Be(MatchKind.Exhibition, "nothing should count towards the season by accident");
        metadata.Referee.Id.Should().Be(RefereeCatalog.ForSeed(11).Id);
    }

    [Test]
    public void A_fixture_records_its_season_and_round_and_needs_both()
    {
        var pell = RefereeCatalog.LoadEmbedded().Single(r => r.Id == "ref-pell");
        var fixture = Play(11, new MatchOptions { Kind = MatchKind.Fixture, Season = "2026-27", Round = 7, Referee = pell })[0].Metadata!;
        var withoutRound = () => new MatchSimulator(NorthBank, ThamesValley, 11, new MatchOptions { Kind = MatchKind.Fixture, Season = "2026-27" });

        fixture.Kind.Should().Be(MatchKind.Fixture);
        fixture.Season.Should().Be("2026-27");
        fixture.Round.Should().Be(7);
        fixture.Referee.Name.Should().Be("Graham Pell");
        withoutRound.Should().Throw<ArgumentException>("invalid options fail when the match is set up, not halfway through it");
    }

    [Test]
    public void Changing_the_referee_changes_the_match_but_it_still_replays_exactly()
    {
        var referees = RefereeCatalog.LoadEmbedded();
        var strict = new MatchOptions { Referee = referees.Single(r => r.Id == "ref-pell") };
        var lenient = new MatchOptions { Referee = referees.Single(r => r.Id == "ref-delacroix") };

        Play(21, strict).Should().BeEquivalentTo(Play(21, strict), options => options.WithStrictOrdering());
        Play(21, strict).Select(e => e.Type).Should().NotEqual(Play(21, lenient).Select(e => e.Type));
    }

    // Advantage and deferred cards

    [Test]
    public void Advantage_keeps_the_move_going_and_is_never_played_for_a_penalty()
    {
        var advantages = _season.SelectMany(m => m.Zip(m.Skip(1))).Where(pair => pair.First.AdvantagePlayed).ToList();

        advantages.Should().NotBeEmpty();
        advantages.Should().OnlyContain(pair => pair.Second.PossessionId == pair.First.PossessionId, "the fouled team keeps the ball");
        _events.Where(e => e.Type == MatchEventType.Foul && PitchDimensions.IsInPenaltyArea(e.Start, e.Team))
            .Should().NotBeEmpty().And.OnlyContain(e => !e.AdvantagePlayed, "a foul in the box is a penalty");
    }

    [Test]
    public void Every_card_names_a_foul_by_the_same_player()
    {
        foreach (var match in _season)
        {
            var bySequence = match.ToDictionary(e => e.Sequence);
            foreach (var card in match.Where(e => e.Type == MatchEventType.Card))
            {
                var foul = bySequence[card.FoulSequence!.Value];
                foul.Type.Should().Be(MatchEventType.Foul);
                foul.PlayerId.Should().Be(card.PlayerId);
                foul.Team.Should().Be(card.Team);
            }
        }
    }

    [Test]
    public void A_card_after_advantage_waits_for_the_next_stoppage()
    {
        var deferred = _season.SelectMany(m => m.Select((card, i) => (Match: m, Card: card, Index: i)))
            .Where(x => x.Card.Type == MatchEventType.Card && x.Match[x.Index - 1].Sequence != x.Card.FoulSequence)
            .ToList();

        deferred.Should().NotBeEmpty("advantage sometimes delays a booking");
        deferred.Should().OnlyContain(x => IsStoppage(x.Match, x.Index - 1) || IsHalfTimeWhistle(x.Match, x.Index),
            "the referee waits until the ball is dead");
    }

    // The ball is dead after a foul that stopped play, another card, the ball going out of
    // play (ADR-022), a goal, or a save the keeper held. A block that stays in play, or a shot
    // back off the woodwork, isn't a stoppage, and neither is anything else.
    private static bool IsStoppage(List<MatchEvent> match, int index)
    {
        return match[index] switch
        {
            { Type: MatchEventType.Foul, AdvantagePlayed: false } or { Type: MatchEventType.Card } or { Type: MatchEventType.BallOutOfPlay } => true,
            { Shot.Outcome: ShotOutcome.Goal or ShotOutcome.Saved } => true,
            _ => false
        };
    }

    private static bool IsHalfTimeWhistle(List<MatchEvent> match, int cardIndex) =>
        match.Skip(cardIndex).SkipWhile(e => e.Type == MatchEventType.Card).First().Type == MatchEventType.PeriodEnd;

    // Referees and temperament

    [Test]
    public void A_strict_referee_books_more_and_a_fussy_one_blows_more()
    {
        // Each referee's 150 matches are played once, then counted as often as needed.
        var referees = RefereeCatalog.LoadEmbedded();
        var seasons = new[] { "ref-pell", "ref-delacroix", "ref-okonkwo-ward" }.ToDictionary(
            id => id,
            id => Enumerable.Range(4000, 150).Select(seed => Play(seed, new MatchOptions { Referee = referees.Single(r => r.Id == id) })).ToList());
        double PerMatch(string refereeId, Func<MatchEvent, bool> count) => seasons[refereeId].Average(m => m.Count(count));

        var pellYellows = PerMatch("ref-pell", e => e.Card == CardColour.Yellow);
        var delacroixYellows = PerMatch("ref-delacroix", e => e.Card == CardColour.Yellow);
        var pellFouls = PerMatch("ref-pell", e => e.Type == MatchEventType.Foul);
        var okonkwoFouls = PerMatch("ref-okonkwo-ward", e => e.Type == MatchEventType.Foul);
        var pellAdvantages = PerMatch("ref-pell", e => e.AdvantagePlayed);
        var okonkwoAdvantages = PerMatch("ref-okonkwo-ward", e => e.AdvantagePlayed);
        TestContext.Out.WriteLine($"Yellows: Pell {pellYellows:0.00}, Delacroix {delacroixYellows:0.00}. Fouls: Pell {pellFouls:0.0}, Okonkwo-Ward {okonkwoFouls:0.0}. Advantages: Pell {pellAdvantages:0.0}, Okonkwo-Ward {okonkwoAdvantages:0.0}");

        pellYellows.Should().BeGreaterThan(delacroixYellows + 1.5);
        pellFouls.Should().BeGreaterThan(okonkwoFouls + 2);
        okonkwoAdvantages.Should().BeGreaterThan(pellAdvantages * 1.5);
    }

    [Test]
    public void A_hot_head_collects_far_more_cards_than_a_calm_team_mate_in_the_same_position()
    {
        // On temperament alone, a 0.2 against a 0.6 would be about 1.9 times the cards. A
        // booked player eases off like anyone else, which takes it to about 1.7, so the test
        // asks for 1.5.
        double CardsPerMatch(string playerId) => _season.Average(m => m.Count(e => e.Type == MatchEventType.Card && e.PlayerId == playerId));

        // Joel Mbeki (0.2) against Thames Valley's other starting defenders (0.55 to 0.7), and
        // Marco Sienna (0.2) against North Bank's other starting midfielders (0.65 and 0.7).
        var mbeki = CardsPerMatch("tvu-05");
        var calmDefenders = new[] { "tvu-02", "tvu-03", "tvu-06" }.Average(CardsPerMatch);
        var sienna = CardsPerMatch("nbfc-08");
        var calmMidfielders = new[] { "nbfc-05", "nbfc-10" }.Average(CardsPerMatch);
        TestContext.Out.WriteLine($"Cards per match: Mbeki {mbeki:0.000} vs {calmDefenders:0.000}, Sienna {sienna:0.000} vs {calmMidfielders:0.000}");

        mbeki.Should().BeGreaterThan(calmDefenders * 1.5);
        sienna.Should().BeGreaterThan(calmMidfielders * 1.5);
    }

    // Height: passes, crosses and headers

    [Test]
    public void Corners_are_high_and_open_play_headers_only_meet_crosses()
    {
        static bool IsCornerDelivery(MatchEvent e) =>
            e.Type == MatchEventType.Pass && e.Start.X is 0 or PitchDimensions.Length && e.Start.Y is 0 or PitchDimensions.Width;

        _events.Where(IsCornerDelivery).Should().NotBeEmpty().And.OnlyContain(e => e.Pass!.Height == PassHeight.High);

        var openPlayHeaders = _season.SelectMany(m => m.Select((e, i) => (Match: m, Shot: e, Index: i)))
            .Where(x => x.Shot.Shot is { BodyPart: BodyPart.Head, Situation: ShotSituation.OpenPlay or ShotSituation.Counter })
            .ToList();

        openPlayHeaders.Should().NotBeEmpty();
        foreach (var (match, shot, index) in openPlayHeaders)
        {
            // Pressure on the header-taker can come between the cross and the header.
            var cross = match.Take(index).Last(e => e.Type != MatchEventType.Pressure);
            cross.Should().Match<MatchEvent>(e => e.Type == MatchEventType.Pass
                && e.Successful
                && e.Pass!.Height == PassHeight.High
                && e.Team == shot.Team
                && PitchDimensions.IsInPenaltyArea(e.End!.Value, e.Team == TeamSide.Home ? TeamSide.Away : TeamSide.Home));
        }
    }

    [Test]
    public void Short_passes_stay_mostly_on_the_ground_and_long_balls_go_up()
    {
        // Corners and throw-ins (from the touchline) are set pieces with their own heights.
        var passes = _events.Where(e => e.Pass is not null && !(e.Start.X is 0 or PitchDimensions.Length) && !(e.Start.Y is 0 or PitchDimensions.Width)).ToList();
        var shortPasses = passes.Where(p => p.Start.DistanceTo(p.End!.Value) < 15).ToList();
        var longPasses = passes.Where(p => p.Start.DistanceTo(p.End!.Value) > 25).ToList();

        shortPasses.Count(p => p.Pass!.Height == PassHeight.Ground).Should().BeGreaterThan(shortPasses.Count * 3 / 4);
        longPasses.Count(p => p.Pass!.Height != PassHeight.Ground).Should().BeGreaterThan(longPasses.Count / 2);
    }

    // Where shots go

    [Test]
    public void Misses_include_some_skied_like_a_rugby_kick_and_goals_sometimes_go_in_off_the_post()
    {
        var shots = _events.Where(e => e.Shot is not null).Select(e => e.Shot!).ToList();

        shots.Should().Contain(s => s.Outcome == ShotOutcome.OffTarget && s.GoalMouth!.Value.Up > 5, "over the bar like a rugby kick");
        shots.Should().Contain(s => s.Outcome == ShotOutcome.OffTarget && Math.Abs(s.GoalMouth!.Value.Across) > 3.9, "wide of the post");
        shots.Should().Contain(s => s.Outcome == ShotOutcome.Goal && s.HitWoodwork, "in off the post or the bar");
        shots.Where(s => s.HitWoodwork && s.Outcome == ShotOutcome.OffTarget)
            .Should().Contain(s => s.GoalMouth!.Value.Up > 2.3, "the bar").And.Contain(s => s.GoalMouth!.Value.Up < 2.3, "a post");
        shots.Where(s => s.BodyPart == BodyPart.Head && s.Outcome == ShotOutcome.OffTarget && !s.HitWoodwork)
            .Should().OnlyContain(s => s.GoalMouth!.Value.Up <= 5, "nobody skies a header like a rugby kick");
    }

    [Test]
    public void A_shot_off_the_woodwork_stays_in_play()
    {
        var gaps = _season.SelectMany(m => m.Zip(m.Skip(1)))
            .Where(pair => pair.First.Shot is { HitWoodwork: true, Outcome: ShotOutcome.OffTarget })
            .Select(pair => pair.Second.Time.Clock - pair.First.Time.Clock)
            .ToList();

        gaps.Should().NotBeEmpty();
        gaps.Should().OnlyContain(gap => gap < TimeSpan.FromSeconds(15), "no goal kick: the rebound is live");
    }

    // Penalties

    [Test]
    public void Every_penalty_records_the_keeper_s_dive_and_takers_and_keepers_show_their_habits()
    {
        var penalties = _events.Where(e => e.Shot is { Situation: ShotSituation.Penalty }).ToList();
        penalties.Should().OnlyContain(p => p.Shot!.KeeperDive != null);

        // Reggie Ashworth aims to his right 80% of the time.
        var ashworth = penalties.Where(p => p.PlayerId == "nbfc-07").Select(p => GoalFrame.ZoneOf(p.Shot!.GoalMouth!.Value)).ToList();
        ashworth.Should().HaveCountGreaterThan(20);
        ashworth.Count(z => z is GoalZone.LowRight or GoalZone.HighRight).Should().BeGreaterThan(ashworth.Count * 6 / 10);

        // Tomas Varga (North Bank's keeper) dives to the taker's left 60% of the time.
        var varga = penalties.Where(p => p.Team == TeamSide.Away).Select(p => p.Shot!.KeeperDive!.Value).ToList();
        varga.Count(d => d == DiveDirection.Left).Should().BeGreaterThan(varga.Count(d => d == DiveDirection.Right));
    }

    // Added after code review

    // High penalties miss about 12% of the time, so that case must include misses. Low ones
    // miss about 3%, and 70 of them can easily have none, so the low case doesn't insist.
    [TestCase(GoalZone.HighLeft, true)]
    [TestCase(GoalZone.LowRight, false)]
    public void Every_penalty_lands_in_the_zone_aimed_at_whether_scored_saved_missed_or_off_the_woodwork(GoalZone aimed, bool mustIncludeMisses)
    {
        // Reggie Ashworth takes North Bank's penalties. Give him a single zone, and every one of
        // his penalties must land in it, so the history a scout reads is true (ADR-021).
        var weights = Enum.GetValues<GoalZone>().Select(z => z == aimed ? 1.0 : 0.0).ToArray();
        var aim = new PenaltyAim(weights[0], weights[1], weights[2], weights[3], weights[4], weights[5]);
        var oneZoneNorthBank = NorthBank with
        {
            Squad = [.. NorthBank.Squad.Select(p => p.Id == "nbfc-07" ? p with { PenaltyAim = aim } : p)]
        };

        var penalties = Enumerable.Range(6000, 600)
            .SelectMany(seed => new MatchSimulator(oneZoneNorthBank, ThamesValley, seed).Play())
            .Where(e => e.Shot is { Situation: ShotSituation.Penalty } && e.PlayerId == "nbfc-07")
            .ToList();
        TestContext.Out.WriteLine($"{penalties.Count} penalties, {penalties.Count(p => p.Shot!.Outcome == ShotOutcome.OffTarget)} missed, {penalties.Count(p => p.Shot!.HitWoodwork)} off the woodwork");

        if (mustIncludeMisses)
        {
            penalties.Should().Contain(p => p.Shot!.Outcome == ShotOutcome.OffTarget, "the test must cover misses too");
        }

        penalties.Should().NotBeEmpty();
        penalties.Should().OnlyContain(p => GoalFrame.ZoneOf(p.Shot!.GoalMouth!.Value) == aimed);
    }

    [Test]
    public void A_replay_gets_its_own_match_id_and_cannot_reuse_its_fixture_s()
    {
        var fixtureId = new MatchSimulator(NorthBank, ThamesValley, 42, new MatchOptions { Kind = MatchKind.Fixture, Season = "2026-27", Round = 1 }).MatchId;
        var replay = new MatchSimulator(NorthBank, ThamesValley, 42, new MatchOptions { Kind = MatchKind.Replay, ReplayOf = fixtureId });
        var reusingTheId = () => new MatchSimulator(NorthBank, ThamesValley, 42, new MatchOptions { Kind = MatchKind.Replay, ReplayOf = fixtureId, MatchId = fixtureId });

        replay.MatchId.Should().NotBe(fixtureId, "a replay under the fixture's ID would overwrite its stored documents");
        replay.MatchId.Should().StartWith(fixtureId);
        replay.Play().First().Metadata!.ReplayOf.Should().Be(fixtureId);
        reusingTheId.Should().Throw<ArgumentException>().WithMessage("*own match ID*");
    }
}
