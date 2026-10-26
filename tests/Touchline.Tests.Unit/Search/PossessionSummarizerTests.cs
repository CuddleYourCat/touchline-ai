using System.Text.Json;
using FluentAssertions;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Search;
using static Touchline.Tests.Unit.Search.SearchTestSupport;

namespace Touchline.Tests.Unit.Search;

[TestFixture]
public sealed class PossessionSummarizerTests
{
    private static readonly KeyMomentDetector _detector = new();

    private static List<SummarizedPossession> Summarize(IReadOnlyList<MatchEvent> events) =>
        [.. PossessionSummarizer.Summarize(MatchId, events, _detector, SmallRoster())];

    [Test]
    public void Events_are_grouped_by_possession_in_sequence_order_whatever_order_they_arrive_in()
    {
        MatchEvent[] events =
        [
            Event(4, 2, MatchEventType.Pass, "p6", TeamSide.Away),
            Event(2, 1, MatchEventType.Pass, "p2"),
            Event(1, 1, MatchEventType.Pass, "p1"),
            Event(3, 1, MatchEventType.Pass, "p3"),
            Event(5, 2, MatchEventType.Carry, "p7", TeamSide.Away)
        ];

        var result = Summarize(events);

        result.Select(r => r.Document.Id).Should().Equal($"{MatchId}_1", $"{MatchId}_2");
        result[0].Document.Players.Should().Equal("PlayerB SurnameB", "PlayerC SurnameC", "PlayerD SurnameD");
        result[0].Document.Team.Should().Be("Home");
        result[1].Document.Team.Should().Be("Away");
        result[0].Document.Summary.Should().Contain("The ball went through PlayerB SurnameB, PlayerC SurnameC, PlayerD SurnameD");
    }

    [Test]
    public void A_group_with_no_on_ball_events_is_skipped()
    {
        MatchEvent[] events =
        [
            Event(1, 1, MatchEventType.Pass, "p1"),
            Event(2, 2, MatchEventType.Pressure, "p2", TeamSide.Away),
            Event(3, 2, MatchEventType.Tackle, "p3", TeamSide.Away),
            Event(4, 3, MatchEventType.Carry, "p4", TeamSide.Away)
        ];

        Summarize(events).Select(r => r.Document.PossessionId).Should().Equal("1", "3");
    }

    [Test]
    public void A_long_move_names_the_first_six_and_last_three_players()
    {
        var events = Enumerable.Range(1, 12)
            .Select(n => Event(n, 1, MatchEventType.Pass, $"p{n}"))
            .ToList();

        var summary = Summarize(events).Single().Document.Summary;

        summary.Should().Contain("PlayerB SurnameB, PlayerC SurnameC, PlayerD SurnameD, PlayerE SurnameE, PlayerF SurnameF, PlayerG SurnameG, then after 3 more touches PlayerK SurnameK, PlayerL SurnameL, PlayerM SurnameM.");
        summary.Should().NotContain("SurnameH").And.NotContain("SurnameJ");
    }

    [Test]
    public void A_player_the_roster_does_not_know_is_left_out_and_never_shown_as_an_id()
    {
        MatchEvent[] events =
        [
            Event(1, 1, MatchEventType.Pass, "p1"),
            Event(2, 1, MatchEventType.Pass, "not-on-the-roster"),
            Event(3, 1, MatchEventType.Pass, "p2")
        ];

        var document = Summarize(events).Single().Document;

        document.Players.Should().Equal("PlayerB SurnameB", "PlayerC SurnameC");
        document.Summary.Should().NotContain("not-on-the-roster");
    }

    [TestCase(MatchEventType.BallOutOfPlay, "out")]
    [TestCase(MatchEventType.Foul, "foul")]
    [TestCase(MatchEventType.PeriodEnd, "period_end")]
    [TestCase(MatchEventType.Interception, "turnover")]
    public void The_last_event_decides_how_the_possession_ended(MatchEventType last, string endedIn)
    {
        var events = new[]
        {
            Event(1, 1, MatchEventType.Pass, "p1"),
            Event(2, 1, last, last == MatchEventType.PeriodEnd ? null : "p8", TeamSide.Away)
        };

        var result = Summarize(events).Single();

        result.Document.EndedIn.Should().Be(endedIn);
        result.KeyMoment.Should().BeNull();
    }

    [Test]
    public void A_possession_that_ends_in_a_goal_is_a_key_moment()
    {
        MatchEvent[] events = [Event(1, 1, MatchEventType.Pass, "p1"), Goal(2, 1, "p2")];

        var result = Summarize(events).Single();

        result.KeyMoment.Should().Be(KeyMomentKind.Goal);
        result.Document.EndedIn.Should().Be("goal");
        result.Document.Summary.Should().EndWith("The move ended in a goal.");
    }

    [Test]
    public void Both_halves_get_sortable_start_seconds_and_clock_text()
    {
        MatchEvent[] events =
        [
            Event(1, 1, MatchEventType.Pass, "p1", minute: 44),
            Event(2, 1, MatchEventType.Pass, "p2", minute: 44),
            Event(3, 2, MatchEventType.Pass, "p3", minute: 63, period: MatchPeriod.SecondHalf),
            Event(4, 2, MatchEventType.Pass, "p4", minute: 63, period: MatchPeriod.SecondHalf)
        ];

        var result = Summarize(events);

        result[0].Document.FromSeconds.Should().Be(44 * 60 + 1);
        result[0].Document.FromClock.Should().Be("44:01");
        result[1].Document.FromSeconds.Should().Be(3600 + 63 * 60 + 3);
        result[1].Document.FromClock.Should().Be("63:03");
        result[1].Document.ToClock.Should().Be("63:04");
    }

    [Test]
    public void Summaries_never_pass_the_cap()
    {
        var long600 = new string('x', 700) + " end";

        PossessionSummarizer.Truncate(long600).Length.Should().BeLessThanOrEqualTo(PossessionSummarizer.MaxSummaryChars);
        PossessionSummarizer.Truncate(long600).Should().EndWith("...");
        PossessionSummarizer.Truncate("short").Should().Be("short");
    }

    [Test]
    public void A_substitution_never_ends_or_joins_a_move()
    {
        var events = SeededMatch();
        var roster = SeededRoster(events);
        var endedByTheBallGoingOut = events
            .GroupBy(e => e.PossessionId)
            .Where(g => g.Last().Type == MatchEventType.Substitution && g.Last(e => e.Type != MatchEventType.Substitution).Type == MatchEventType.BallOutOfPlay)
            .Select(g => g.Key.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToHashSet();

        var possessions = PossessionSummarizer.Summarize(MatchId, events, _detector, roster);

        endedByTheBallGoingOut.Should().NotBeEmpty();
        possessions.Where(p => endedByTheBallGoingOut.Contains(p.Document.PossessionId))
            .Should().OnlyContain(p => p.Document.EndedIn == "out", "a substitution happens at a stoppage, outside any move (ADR-064)");
    }

    [Test]
    public void A_seeded_match_gives_the_same_documents_every_time_and_every_summary_is_capped()
    {
        var events = SeededMatch();
        var roster = SeededRoster(events);

        var first = PossessionSummarizer.Summarize(MatchId, events, _detector, roster);
        var second = PossessionSummarizer.Summarize(MatchId, [.. events.AsEnumerable().Reverse()], _detector, roster);

        Serialize(first).Should().Be(Serialize(second));
        first.Should().HaveCount(213);
        first.Select(p => p.Document.Id).Should().OnlyHaveUniqueItems().And.OnlyContain(id => id.StartsWith(MatchId + "_", StringComparison.Ordinal));
        first.Should().OnlyContain(p => p.Document.Summary.Length <= PossessionSummarizer.MaxSummaryChars);
        first.Should().OnlyContain(p => p.Document.Players.All(name => roster.PlayerNames.Values.Contains(name)));
        first.Select(p => p.Document.FromSeconds).Should().OnlyContain(s => s >= 0).And.Contain(s => s >= 3600);
        first.Where(p => p.KeyMoment == KeyMomentKind.Goal).Should().HaveCount(3);
        first[0].Document.Summary.Should().StartWith("North Bank FC possession from 00:00 to 00:30. The ball went through Theo Lindqvist, Marco Sienna");
    }

    private static string Serialize(IEnumerable<SummarizedPossession> possessions) => JsonSerializer.Serialize(possessions);
}
