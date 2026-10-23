using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
using Touchline.Domain.Teams;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Demo;
using Touchline.MatchCentre.Squad;

namespace Touchline.Tests.Unit.MatchCentre;

[TestFixture]
public sealed class MatchFactsAdapterTests
{
    private static readonly IReadOnlyDictionary<string, TeamProfile> _teams = TeamProfileCatalog.LoadEmbedded();

    private MatchFactsAdapter _facts = null!;
    private DemoMatchSource _source = null!;
    private FakeTimeProvider _time = null!;

    [SetUp]
    public void PlayTheDemoMatchToFullTime()
    {
        var time = _time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 14, 0, 0, TimeSpan.Zero));
        var source = _source = new DemoMatchSource(
            DemoRecording.Events,
            _teams,
            new KeyMomentDetector(teams: _teams.Values),
            Options.Create(new DemoOptions { Speed = 1, LapGap = TimeSpan.FromHours(10) }),
            time);
        time.Advance(TimeSpan.FromHours(3));
        _facts = new MatchFactsAdapter(source);
    }

    [Test]
    public async Task The_summary_carries_the_score_and_the_names_the_guardrail_allows()
    {
        var sheet = await _facts.GetSummaryAsync("demo-1", CancellationToken.None);

        sheet.Should().NotBeNull();
        sheet!.FullTime.Should().BeTrue();
        sheet.HomePlayers.Should().HaveCount(11);
        sheet.AwayPlayers.Should().HaveCount(11);
        sheet.OtherKnownNames.Should().Contain([sheet.HomeTeam, sheet.AwayTeam]);
    }

    [Test]
    public async Task An_unknown_match_has_no_facts()
    {
        (await _facts.GetSummaryAsync("nope", CancellationToken.None)).Should().BeNull();
        (await _facts.GetKeyMomentsAsync("nope", CancellationToken.None)).Should().BeEmpty();
        (await _facts.GetEventsAsync("nope", 0, 90, CancellationToken.None)).Should().BeEmpty();
    }

    [Test]
    public async Task Key_moments_are_capped_and_carry_moment_ids()
    {
        var moments = await _facts.GetKeyMomentsAsync("demo-1", CancellationToken.None);

        moments.Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(MatchFactLimits.MaxMoments);
        moments.Should().OnlyContain(m => m.Id.StartsWith("demo-1:", StringComparison.Ordinal));
    }

    [Test]
    public async Task Events_are_capped_notable_and_inside_the_minute_range()
    {
        var all = await _facts.GetEventsAsync("demo-1", 0, 120, CancellationToken.None);
        var window = await _facts.GetEventsAsync("demo-1", 60, 70, CancellationToken.None);

        all.Should().HaveCount(MatchFactLimits.MaxEvents);
        all.Should().OnlyContain(e => e.Type != MatchEventType.Pass || e.Outcome == "corner");
        window.Should().HaveCountLessThanOrEqualTo(MatchFactLimits.MaxEvents)
            .And.OnlyContain(e => DemoRecording.Events.Any(x => MatchFactIds.Event("demo-1", x.Sequence) == e.Id
                && x.Time.Clock >= TimeSpan.FromMinutes(60) && x.Time.Clock < TimeSpan.FromMinutes(71)));
    }

    [Test]
    public async Task A_late_window_starts_its_scan_near_the_minute_and_misses_nothing()
    {
        var reads = new Mock<IMatchReadModel>();
        reads.Setup(r => r.GetMatchAsync("demo-1", It.IsAny<CancellationToken>()))
            .Returns((string id, CancellationToken token) => _source.GetMatchAsync(id, token));
        reads.Setup(r => r.GetEventsAsync("demo-1", It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string id, long after, int limit, CancellationToken token) => _source.GetEventsAsync(id, after, limit, token));
        var facts = new MatchFactsAdapter(reads.Object);
        var expected = DemoRecording.Events
            .TakeWhile(e => (int)e.Time.Clock.TotalMinutes <= 85)
            .Where(e => (int)e.Time.Clock.TotalMinutes >= 80
                && (e.Type is MatchEventType.Shot or MatchEventType.Foul or MatchEventType.Card || e.Restart == RestartKind.Corner))
            .Take(MatchFactLimits.MaxEvents)
            .Select(e => MatchFactIds.Event("demo-1", e.Sequence));

        var window = await facts.GetEventsAsync("demo-1", 80, 85, CancellationToken.None);

        window.Select(e => e.Id).Should().Equal(expected);
        reads.Verify(r => r.GetEventsAsync("demo-1", 0, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task One_question_reads_the_match_once_and_events_carry_an_event_prefix()
    {
        var reads = new Mock<IMatchReadModel>();
        reads.Setup(r => r.GetMatchAsync("demo-1", It.IsAny<CancellationToken>()))
            .Returns((string id, CancellationToken token) => _source.GetMatchAsync(id, token));
        reads.Setup(r => r.GetEventsAsync("demo-1", It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string id, long after, int limit, CancellationToken token) => _source.GetEventsAsync(id, after, limit, token));
        var facts = new MatchFactsAdapter(reads.Object, time: _time);

        await facts.GetSummaryAsync("demo-1", CancellationToken.None);
        await facts.GetEventsAsync("demo-1", 0, 10, CancellationToken.None);
        var events = await facts.GetEventsAsync("demo-1", 10, 20, CancellationToken.None);

        reads.Verify(r => r.GetMatchAsync("demo-1", It.IsAny<CancellationToken>()), Times.Once);
        events.Should().NotBeEmpty().And.OnlyContain(e => e.Id.StartsWith("event:demo-1:", StringComparison.Ordinal));

        _time.Advance(MatchFactsAdapter.CacheFor);
        await facts.GetEventsAsync("demo-1", 10, 20, CancellationToken.None);
        reads.Verify(r => r.GetMatchAsync("demo-1", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task The_roster_is_the_whole_squad_from_the_team_profiles()
    {
        var facts = new MatchFactsAdapter(_source, new InMemoryTeamStore(_teams));

        var sheet = await facts.GetSummaryAsync("demo-1", CancellationToken.None);

        var substitutes = sheet!.SquadNames.Except(sheet.HomePlayers.Concat(sheet.AwayPlayers)).ToList();
        substitutes.Should().NotBeEmpty("a substitute's name must pass the guardrail");
        sheet.AllowedPlayerNames.Should().Contain(substitutes).And.Contain(sheet.HomePlayers);
    }

    [Test]
    public async Task Without_a_profile_the_line_ups_are_the_roster()
    {
        var store = new Mock<ITeamStore>();
        store.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException("The team store is down."));
        var facts = new MatchFactsAdapter(_source, store.Object);

        var sheet = await facts.GetSummaryAsync("demo-1", CancellationToken.None);

        sheet!.SquadNames.Should().BeEquivalentTo(sheet.HomePlayers.Concat(sheet.AwayPlayers).Distinct());
    }
}
