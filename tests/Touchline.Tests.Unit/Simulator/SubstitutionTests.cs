extern alias simulator;
using System.Globalization;
using FluentAssertions;
using FluentAssertions.Execution;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain.Matches;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;

namespace Touchline.Tests.Unit.Simulator;

/// <summary>
/// Substitutions (ADR-064): up to five per team in up to three stoppage windows plus half time,
/// never the goalkeeper, like for like with a reason, and only at a stoppage. Seeds are fixed, so
/// the statistical checks are exact and repeatable.
/// </summary>
[TestFixture]
public class SubstitutionTests
{
    private const int _matches = 300;

    private static readonly IReadOnlyDictionary<string, TeamProfile> _teams = TeamProfileCatalog.LoadEmbedded();
    private static TeamProfile NorthBank => _teams["north-bank-fc"];
    private static TeamProfile ThamesValley => _teams["thames-valley-united"];

    private List<List<MatchEvent>> _season = null!;

    [OneTimeSetUp]
    public void PlayTheSeason() =>
        _season = [.. Enumerable.Range(3000, _matches).Select(seed => new MatchSimulator(NorthBank, ThamesValley, seed).Play().ToList())];

    private static Player PlayerOf(string id) => NorthBank.FindPlayer(id) ?? ThamesValley.FindPlayer(id)!;

    // Each substitution, with the event that stopped play before it (cards and other substitutions skipped).
    private static IEnumerable<(MatchEvent Substitution, MatchEvent Stoppage)> Substitutions(List<MatchEvent> match) =>
        match.Select((e, i) => (Event: e, Index: i))
            .Where(x => x.Event.Type == MatchEventType.Substitution)
            .Select(x => (x.Event, match.Take(x.Index).Last(e => e.Type is not (MatchEventType.Card or MatchEventType.Substitution))));

    private static bool AtHalfTime(MatchEvent stoppage) => stoppage is { Type: MatchEventType.PeriodEnd, Time.Period: MatchPeriod.FirstHalf };

    [Test]
    public void The_default_bench_is_the_squad_players_not_in_the_eleven()
    {
        var metadata = _season[0][0].Metadata!;

        metadata.Home.Substitutes.Should().HaveCount(5, "a 16-player squad leaves five on the bench")
            .And.BeEquivalentTo(NorthBank.Squad.Select(p => p.Id).Except(metadata.Home.StartingEleven));
        metadata.Away.Substitutes.Should().HaveCount(5)
            .And.BeEquivalentTo(ThamesValley.Squad.Select(p => p.Id).Except(metadata.Away.StartingEleven));
    }

    [Test]
    public void A_team_makes_at_most_five_substitutions_in_at_most_three_windows_and_half_time_is_free()
    {
        using var scope = new AssertionScope();
        var halfTimeChanges = 0;
        foreach (var match in _season)
        {
            foreach (var team in Substitutions(match).GroupBy(s => s.Substitution.Team))
            {
                team.Count().Should().BeLessThanOrEqualTo(5);
                team.Where(s => !AtHalfTime(s.Stoppage)).Select(s => s.Stoppage.Sequence).Distinct().Count()
                    .Should().BeLessThanOrEqualTo(3, "half time doesn't use a window");
                halfTimeChanges += team.Count(s => AtHalfTime(s.Stoppage));
            }
        }

        halfTimeChanges.Should().BePositive("some teams change at half time");
    }

    [Test]
    public void A_substitution_happens_only_at_a_stoppage()
    {
        var stoppages = _season.SelectMany(Substitutions).Select(s => s.Stoppage).ToList();

        stoppages.Should().NotBeEmpty();
        stoppages.Should().OnlyContain(e =>
            e.Type == MatchEventType.BallOutOfPlay
            || (e.Type == MatchEventType.Foul && !e.AdvantagePlayed)
            || e.IsGoal
            || (e.Type == MatchEventType.PeriodEnd && e.Time.Period == MatchPeriod.FirstHalf));
    }

    [Test]
    public void No_substitution_comes_between_a_penalty_being_given_and_taken()
    {
        var penalties = 0;
        foreach (var match in _season)
        {
            foreach (var (shot, index) in match.Select((e, i) => (e, i)).Where(x => x.e.Shot?.Situation == ShotSituation.Penalty))
            {
                var foul = match.Take(index).Last(e => e.Type == MatchEventType.Foul);
                match.Where(e => e.Sequence > foul.Sequence && e.Sequence < shot.Sequence)
                    .Should().NotContain(e => e.Type == MatchEventType.Substitution, "the kick is taken by a player who was on when it was given");
                penalties++;
            }
        }

        penalties.Should().BeGreaterThan(30);
    }

    [Test]
    public void The_goalkeeper_a_sent_off_player_and_a_substituted_player_never_come_off()
    {
        using var scope = new AssertionScope();
        foreach (var match in _season)
        {
            var metadata = match[0].Metadata!;
            var gone = new HashSet<string>();
            var bench = metadata.Home.Substitutes.Concat(metadata.Away.Substitutes).ToHashSet();
            foreach (var e in match)
            {
                if (e is { Type: MatchEventType.Card, Card: CardColour.Red })
                {
                    gone.Add(e.PlayerId!);
                }

                if (e.Type != MatchEventType.Substitution)
                {
                    continue;
                }

                PlayerOf(e.PlayerId!).Position.Should().NotBe(PlayerPosition.Goalkeeper, "the goalkeeper is never substituted");
                PlayerOf(e.ReplacementPlayerId!).Position.Should().NotBe(PlayerPosition.Goalkeeper);
                gone.Should().NotContain(e.PlayerId!, "a player sent off or already substituted has left the pitch");
                bench.Should().Contain(e.ReplacementPlayerId!, "only a substitute can come on");
                bench.Should().NotContain(e.PlayerId!, "a substitute who came on stays on");
                gone.Add(e.PlayerId!);
                bench.Remove(e.ReplacementPlayerId!);
            }
        }
    }

    [Test]
    public void Every_substitution_is_a_valid_event_on_the_halfway_line()
    {
        var substitutions = _season.SelectMany(m => m).Where(e => e.Type == MatchEventType.Substitution).ToList();

        substitutions.Should().OnlyContain(e => e.Start.X == 52.5 && e.Start.Y == 0 && e.End == null);
        foreach (var e in substitutions)
        {
            e.EnsureValid();
        }
    }

    [Test]
    public void The_same_seed_makes_the_same_substitutions_and_another_seed_different_ones()
    {
        static List<(long, string?, string?)> Subs(int seed) =>
            [.. new MatchSimulator(NorthBank, ThamesValley, seed).Play()
                .Where(e => e.Type == MatchEventType.Substitution)
                .Select(e => (e.Sequence, e.PlayerId, e.ReplacementPlayerId))];

        Subs(42).Should().Equal(Subs(42)).And.NotBeEmpty();
        Subs(42).Should().NotEqual(Subs(43));
    }

    [Test]
    public void Most_substitutions_come_between_55_and_85_minutes()
    {
        var minutes = _season.SelectMany(Substitutions).Where(s => !AtHalfTime(s.Stoppage)).Select(s => s.Substitution.Time.Clock.TotalMinutes).ToList();

        minutes.Should().OnlyContain(m => m >= 55, "a window opens at the first stoppage after its minute");
        minutes.Count(m => m <= 86).Should().BeGreaterThan((int)(minutes.Count * 0.9));
    }

    [Test]
    public void A_team_behind_late_brings_on_forwards_and_a_team_ahead_takes_them_off()
    {
        var behind = new List<(Player Off, Player On)>();
        var ahead = new List<(Player Off, Player On)>();
        foreach (var match in _season)
        {
            var state = MatchState.KickOff(match[0].MatchId);
            foreach (var e in match)
            {
                if (e.Type == MatchEventType.Substitution && e.Time.Clock.TotalMinutes >= 70)
                {
                    var (own, other) = e.Team == TeamSide.Home ? (state.Home.Goals, state.Away.Goals) : (state.Away.Goals, state.Home.Goals);
                    var swap = (PlayerOf(e.PlayerId!), PlayerOf(e.ReplacementPlayerId!));
                    (own < other ? behind : own > other ? ahead : []).Add(swap);
                }

                state = state.Apply(e);
            }
        }

        static double Share(List<(Player Off, Player On)> swaps, Func<(Player Off, Player On), bool> kind) => (double)swaps.Count(kind) / swaps.Count;
        static bool MoreAttacking((Player Off, Player On) s) => s.On.Position == PlayerPosition.Forward && s.Off.Position != PlayerPosition.Forward;
        static bool MoreDefensive((Player Off, Player On) s) => s.Off.Position == PlayerPosition.Forward && s.On.Position != PlayerPosition.Forward;

        TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"behind: {behind.Count} late swaps, {Share(behind, MoreAttacking):P0} more attacking; ahead: {ahead.Count}, {Share(ahead, MoreDefensive):P0} more defensive"));
        behind.Should().HaveCountGreaterThan(50);
        ahead.Should().HaveCountGreaterThan(50);
        // Not every late change is tactical, and a bench runs out of forwards, so the test compares the two sides.
        Share(behind, MoreAttacking).Should().BeGreaterThan(0.2, "a team behind swaps a defender or midfielder for a forward");
        Share(behind, MoreAttacking).Should().BeGreaterThan(3 * Share(ahead, MoreAttacking), "a team ahead has no reason to add a forward");
        Share(ahead, MoreDefensive).Should().BeGreaterThan(0.2, "a team ahead swaps a forward for a defender or midfielder");
        Share(ahead, MoreDefensive).Should().BeGreaterThan(3 * Share(behind, MoreDefensive), "a team behind has no reason to take a forward off");
    }

    [Test]
    public void A_booked_player_is_likelier_to_come_off()
    {
        // Outfield starters still on at the 55th minute, split by whether they were booked by then.
        int bookedOff = 0, booked = 0, cleanOff = 0, clean = 0;
        foreach (var match in _season)
        {
            var metadata = match[0].Metadata!;
            var cutoff = match.FindIndex(e => e.Time.Period == MatchPeriod.SecondHalf && e.Time.Clock.TotalMinutes >= 55);
            var before = match.Take(cutoff).ToList();
            var bookedBy55 = before.Where(e => e is { Type: MatchEventType.Card, Card: CardColour.Yellow }).Select(e => e.PlayerId!).ToHashSet();
            var goneBy55 = before.Where(e => e.Type == MatchEventType.Substitution || e is { Type: MatchEventType.Card, Card: CardColour.Red })
                .Select(e => e.PlayerId!).ToHashSet();
            var offLater = match.Skip(cutoff).Where(e => e.Type == MatchEventType.Substitution).Select(e => e.PlayerId!).ToHashSet();
            var redLater = match.Skip(cutoff).Where(e => e is { Type: MatchEventType.Card, Card: CardColour.Red }).Select(e => e.PlayerId!).ToHashSet();

            foreach (var id in metadata.Home.StartingEleven.Skip(1).Concat(metadata.Away.StartingEleven.Skip(1)))
            {
                if (goneBy55.Contains(id) || redLater.Contains(id))
                {
                    continue;
                }

                if (bookedBy55.Contains(id))
                {
                    booked++;
                    bookedOff += offLater.Contains(id) ? 1 : 0;
                }
                else
                {
                    clean++;
                    cleanOff += offLater.Contains(id) ? 1 : 0;
                }
            }

        }

        var bookedRate = (double)bookedOff / booked;
        var cleanRate = (double)cleanOff / clean;
        TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"booked: {bookedOff}/{booked} came off ({bookedRate:P0}); not booked: {cleanOff}/{clean} ({cleanRate:P0})"));
        booked.Should().BeGreaterThan(100);
        bookedRate.Should().BeGreaterThan(cleanRate * 1.5, "a booked player is three times as likely to be picked to come off");
    }

    [Test]
    public void A_season_of_substitutions_looks_like_modern_football()
    {
        var perTeam = _season.Select(m => m.Count(e => e is { Type: MatchEventType.Substitution, Team: TeamSide.Home }))
            .Concat(_season.Select(m => m.Count(e => e is { Type: MatchEventType.Substitution, Team: TeamSide.Away })))
            .Average();
        var minute = _season.SelectMany(m => m).Where(e => e.Type == MatchEventType.Substitution).Average(e => e.Time.Clock.TotalMinutes);

        TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{perTeam:F2} substitutions per team, at {minute:F1} minutes on average"));
        perTeam.Should().BeInRange(2.5, 4.5, "most teams use most of their outfield bench of four");
        minute.Should().BeInRange(62, 76);
    }

    // Lineup, directly.

    [Test]
    public void A_substitute_takes_the_departing_player_s_place_and_the_bench_shrinks()
    {
        var lineup = new Lineup(NorthBank);
        var off = lineup.OnPitch.First(p => p.Position == PlayerPosition.Midfielder);
        var on = lineup.OnBench.First(p => p.Position != PlayerPosition.Goalkeeper);
        var index = lineup.OnPitch.ToList().IndexOf(off);

        lineup.Substitute(off, on);

        lineup.OnPitch[index].Should().Be(on);
        lineup.OnPitch.Should().NotContain(off);
        lineup.OnBench.Should().NotContain(on);
        lineup.SubstitutesUsed.Should().Be(1);
        lineup.CameOn(on).Should().BeTrue();
    }

    [Test]
    public void The_goalkeeper_can_t_be_substituted()
    {
        var lineup = new Lineup(NorthBank);

        lineup.Invoking(l => l.Substitute(l.Goalkeeper!, l.OnBench[0])).Should().Throw<InvalidOperationException>().WithMessage("*goalkeeper*");
    }

    [Test]
    public void A_player_sent_off_or_already_substituted_can_t_come_off_and_a_substitute_can_t_come_on_twice()
    {
        var lineup = new Lineup(NorthBank);
        var outfield = lineup.OnPitch.Where(p => p.Position != PlayerPosition.Goalkeeper).ToList();
        var bench = lineup.OnBench.Where(p => p.Position != PlayerPosition.Goalkeeper).ToList();
        lineup.SendOff(outfield[0]);
        lineup.Substitute(outfield[1], bench[0]);

        lineup.Invoking(l => l.Substitute(outfield[0], bench[1])).Should().Throw<InvalidOperationException>("he was sent off");
        lineup.Invoking(l => l.Substitute(outfield[1], bench[1])).Should().Throw<InvalidOperationException>("he has already gone off");
        lineup.Invoking(l => l.Substitute(outfield[2], bench[0])).Should().Throw<InvalidOperationException>("he is already on");
    }

    [Test]
    public void A_sheet_s_bench_is_its_substitutes_and_a_sheet_without_one_has_none()
    {
        var sheet = new MatchdaySheet(
            ["nbfc-01", "nbfc-02", "nbfc-03", "nbfc-04", "nbfc-05", "nbfc-06", "nbfc-07", "nbfc-08", "nbfc-09", "nbfc-10", "nbfc-11"],
            ["nbfc-14", "nbfc-15"]);

        new Lineup(NorthBank, sheet).OnBench.Select(p => p.Id).Should().Equal("nbfc-14", "nbfc-15");
        new Lineup(NorthBank, sheet with { Substitutes = [] }).OnBench.Should().BeEmpty();
    }
}
