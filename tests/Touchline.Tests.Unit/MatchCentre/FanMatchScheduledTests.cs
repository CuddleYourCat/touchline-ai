extern alias simulator;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using simulator::Touchline.Simulator.Publishing;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Storage;
using Touchline.MatchCentre.TeamBuilding;
using Touchline.Storage;

namespace Touchline.Tests.Unit.MatchCentre;

/// <summary>
/// A fan match between its 202 and its first event, and the pick-up deadline (ADR-050).
/// </summary>
[TestFixture]
public class FanMatchScheduledTests
{
    private const string _matchId = "exh-202610241530-77aa77aa";
    private static readonly DateTimeOffset _created = new(2026, 10, 24, 15, 30, 0, TimeSpan.Zero);
    private static readonly FanTeamSpec _fan = TestFanTeams.Standard("fan-test-rovers", ManagerStyle.Possession);
    private static readonly MatchdaySheet _sheet = new([_fan.Players[11].Id, .. _fan.Players.Skip(1).Take(10).Select(p => p.Id)], []);

    private FakeTimeProvider _time = null!;
    private Mock<IMatchReadModel> _inner = null!;
    private Mock<IFanMatchRequestStore> _requests = null!;
    private FanMatchRequest _request = null!;
    private List<(FanMatchStatus Status, string? Reason)> _patches = null!;

    [SetUp]
    public void CreateMocks()
    {
        _time = new FakeTimeProvider(_created);
        _request = Request(FanMatchStatus.Scheduled);
        _patches = [];
        _inner = new Mock<IMatchReadModel>();
        _requests = new Mock<IFanMatchRequestStore>();
        _requests.Setup(r => r.GetAsync(_matchId, It.IsAny<CancellationToken>())).ReturnsAsync(() => _request);
        _requests.Setup(r => r.SetStatusAsync(_matchId, It.IsAny<FanMatchStatus>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<FanMatchStatus>>(), It.IsAny<CancellationToken>()))
            .Callback((string _, FanMatchStatus status, string? reason, IReadOnlyCollection<FanMatchStatus> _, CancellationToken _) => _patches.Add((status, reason)))
            .ReturnsAsync(true);
    }

    [Test]
    public async Task A_scheduled_fan_match_is_served_from_its_request_with_the_engine_s_line_ups_and_no_events()
    {
        _time.Advance(TimeSpan.FromSeconds(40));

        var detail = await ReadModel().GetMatchAsync(_matchId, CancellationToken.None);

        detail.Should().NotBeNull();
        detail!.Summary.Status.Should().Be(MatchStatus.Scheduled);
        detail.Summary.Kind.Should().Be(MatchKind.Exhibition);
        detail.Summary.Home.Name.Should().Be(_fan.Name);
        detail.Summary.KickOffUtc.Should().Be(_request.KickOffUtc);
        detail.Summary.DataSource.Should().Be("synthetic");
        detail.State.LastSequence.Should().Be(0);

        var metadata = FanMatchPlans.CreateSimulator(_request).Play().First().Metadata!;
        detail.HomeLineup.Select(p => p.Id).Should().Equal(metadata.Home.StartingEleven, "the sheet's eleven, in the engine's line order");
        detail.AwayLineup.Select(p => p.Id).Should().Equal(metadata.Away.StartingEleven, "the default eleven the engine will pick");
        detail.HomeLineup.Select(p => p.Name).Should().BeSubsetOf(_fan.Players.Select(p => p.Name));
        detail.Venue.Should().Be(metadata.Venue);
        detail.Referee.Should().Be(metadata.Referee.Name);

        (await ReadModel().GetEventsAsync(_matchId, 0, 100, CancellationToken.None))!.Events.Should().BeEmpty();
        (await ReadModel().GetMomentsAsync(_matchId, Viewer.Default, CancellationToken.None)).Should().BeEmpty();
        (await ReadModel().GetMomentumAsync(_matchId, CancellationToken.None)).Should().BeEmpty();
        _patches.Should().BeEmpty();
    }

    [Test]
    public async Task A_stored_match_comes_from_the_read_side_without_reading_the_request()
    {
        var stored = new MatchDetail(null!, "Venue", "Ref", [], [], null!);
        _inner.Setup(m => m.GetMatchAsync(_matchId, It.IsAny<CancellationToken>())).ReturnsAsync(stored);

        (await ReadModel().GetMatchAsync(_matchId, CancellationToken.None)).Should().BeSameAs(stored);
        _requests.Verify(r => r.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task An_id_that_is_not_a_fan_match_s_stays_a_404_without_a_request_read()
    {
        (await ReadModel().GetMatchAsync("north-bank-fc-thames-valley-united-42", CancellationToken.None)).Should().BeNull();
        _requests.Verify(r => r.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task A_failed_fan_match_is_gone()
    {
        _request = Request(FanMatchStatus.Failed);

        var act = () => ReadModel().GetMatchAsync(_matchId, CancellationToken.None);

        await act.Should().ThrowAsync<FanMatchNotStartedException>();
    }

    [Test]
    public async Task Chaos_a_start_that_never_ran_is_marked_failed_once_past_its_pick_up_deadline()
    {
        _time.Advance(new FanMatchOptions().ScheduledFor + TimeSpan.FromSeconds(1));

        var act = () => ReadModel().GetMatchAsync(_matchId, CancellationToken.None);

        await act.Should().ThrowAsync<FanMatchNotStartedException>();
        _patches.Should().Equal((FanMatchStatus.Failed, FanMatchRequestStatus.NotPickedUpReason));
        _requests.Verify(r => r.SetStatusAsync(_matchId, FanMatchStatus.Failed, It.IsAny<string?>(),
            It.Is<IReadOnlyCollection<FanMatchStatus>>(from => from.SequenceEqual(new[] { FanMatchStatus.Scheduled })), It.IsAny<CancellationToken>()));
    }

    [Test]
    public async Task A_late_pick_up_by_the_job_wins_over_the_deadline()
    {
        _time.Advance(new FanMatchOptions().ScheduledFor + TimeSpan.FromSeconds(1));
        _requests.Setup(r => r.SetStatusAsync(_matchId, FanMatchStatus.Failed, It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<FanMatchStatus>>(), It.IsAny<CancellationToken>()))
            .Callback(() => _request = Request(FanMatchStatus.Running))
            .ReturnsAsync(false);

        (await ReadModel().GetMatchAsync(_matchId, CancellationToken.None))!.Summary.Status.Should().Be(MatchStatus.Scheduled);
    }

    [Test]
    public async Task A_stale_scheduled_request_no_longer_holds_its_teams_but_a_running_one_does()
    {
        _requests.Setup(r => r.ListActiveAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => [_request]);
        var launcher = Launcher();

        (await launcher.IsTeamPlayingAsync(_fan.Id, CancellationToken.None)).Should().BeTrue("a fresh request is about to be picked up");

        _time.Advance(TimeSpan.FromMinutes(4));
        (await launcher.IsTeamPlayingAsync(_fan.Id, CancellationToken.None)).Should().BeFalse("nothing picked it up within three minutes");
        _patches.Should().Equal((FanMatchStatus.Failed, FanMatchRequestStatus.NotPickedUpReason));

        _request = Request(FanMatchStatus.Running);
        (await launcher.IsTeamPlayingAsync(_fan.Id, CancellationToken.None)).Should().BeTrue("a running match keeps the 35-minute window");

        _time.Advance(TimeSpan.FromMinutes(32));
        (await launcher.IsTeamPlayingAsync(_fan.Id, CancellationToken.None)).Should().BeFalse("no match runs that long");
    }

    [TestCase(FanMatchStatus.Scheduled, 2, FanMatchLiveness.Active)]
    [TestCase(FanMatchStatus.Scheduled, 4, FanMatchLiveness.NotPickedUp)]
    [TestCase(FanMatchStatus.Running, 30, FanMatchLiveness.Active)]
    [TestCase(FanMatchStatus.Running, 40, FanMatchLiveness.Over)]
    [TestCase(FanMatchStatus.Finished, 1, FanMatchLiveness.Over)]
    [TestCase(FanMatchStatus.Failed, 1, FanMatchLiveness.Over)]
    public void Liveness_follows_the_two_deadlines(FanMatchStatus status, int minutes, FanMatchLiveness expected) =>
        Request(status).LivenessAt(_created.AddMinutes(minutes), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(35)).Should().Be(expected);

    [Test]
    public void A_match_s_detectors_are_built_once_per_profiles()
    {
        var profiles = _request.TeamsWith(TeamProfileCatalog.LoadEmbedded());
        var key = new KeyMomentDetector();
        var notable = new NotableMomentDetector(key);

        key.ForTeams(profiles).Should().BeSameAs(key.ForTeams(profiles));
        notable.ForTeams(profiles).Should().BeSameAs(notable.ForTeams(profiles));
        key.ForTeams(_request.TeamsWith(TeamProfileCatalog.LoadEmbedded())).Should().NotBeSameAs(key.ForTeams(profiles));
    }

    private static FanMatchRequest Request(FanMatchStatus status) => new(
        _matchId, _created, 404, _created.AddSeconds(5),
        new FanMatchSide(_fan.Id, _fan, _sheet), new FanMatchSide("north-bank-fc", null, null), status);

    private FanMatchReadModel ReadModel() => new(_inner.Object, _requests.Object, Options.Create(new FanMatchOptions()), _time);

    private FanMatchLauncher Launcher() => new(
        Mock.Of<ITeamStore>(), _requests.Object, Mock.Of<IFanMatchLimitStore>(), Mock.Of<ISimulatorJob>(), _time, Options.Create(new FanMatchOptions()), NullLogger<FanMatchLauncher>.Instance);
}
