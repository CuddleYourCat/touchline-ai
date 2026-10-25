using System.Text.Json;
using Azure.ResourceManager.AppContainers.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.TeamBuilding;
using Touchline.Storage;

namespace Touchline.Tests.Unit.MatchCentre;

/// <summary>Starting a fan match on the pipeline (ADR-050): the request, the job and its template.</summary>
[TestFixture]
public class FanMatchLauncherTests
{
    private const string _fanId = "fan-test-rovers";
    private const string _builtInId = "north-bank-fc";

    private static readonly FanTeamSpec _fan = TestFanTeams.Standard(_fanId, ManagerStyle.HighPress);
    private static readonly MatchdaySheet _sheet = new(
        [.. _fan.Players.Take(11).Select(p => p.Id)],
        [.. _fan.Players.Skip(11).Select(p => p.Id)]);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 15, 30, 0, TimeSpan.Zero));
    private Mock<ITeamStore> _teams = null!;
    private Mock<IFanMatchRequestStore> _requests = null!;
    private Mock<ISimulatorJob> _job = null!;
    private Mock<IFanMatchLimitStore> _limits = null!;
    private List<string> _calls = null!;
    private FanMatchRequest? _written;

    [SetUp]
    public void CreateMocks()
    {
        _calls = [];
        _written = null;
        _teams = new Mock<ITeamStore>();
        _teams.Setup(t => t.GetAsync(_fanId, It.IsAny<CancellationToken>())).ReturnsAsync(new StoredTeam(Detail(_fan), "hash"));
        _teams.Setup(t => t.GetAsync(_builtInId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredTeam(TeamViews.FromProfile(TeamProfileCatalog.LoadEmbedded()[_builtInId]), null));

        _requests = new Mock<IFanMatchRequestStore>();
        _requests.Setup(r => r.CreateAsync(It.IsAny<FanMatchRequest>(), It.IsAny<CancellationToken>()))
            .Callback((FanMatchRequest request, CancellationToken _) =>
            {
                _calls.Add("create");
                _written = request;
            })
            .ReturnsAsync(true);
        _requests.Setup(r => r.SetStatusAsync(It.IsAny<string>(), It.IsAny<FanMatchStatus>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<FanMatchStatus>>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("status"))
            .ReturnsAsync(true);

        _limits = new Mock<IFanMatchLimitStore>();
        _limits.Setup(l => l.TryTakeDaySlotAsync(It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _requests.Setup(r => r.ListActiveAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        _job = new Mock<ISimulatorJob>();
        _job.Setup(j => j.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Callback(() => _calls.Add("start")).Returns(Task.CompletedTask);
    }

    [Test]
    public async Task A_launch_writes_the_request_with_snapshots_then_starts_the_job_with_its_id()
    {
        var launch = await Launcher().LaunchAsync(new MatchRequest(_fanId, _builtInId, Home: _sheet, Seed: 77), null, CancellationToken.None);

        launch.Outcome.Should().Be(LaunchOutcome.Accepted);
        _calls.Should().Equal("create", "start");
        var request = _written!;
        launch.Match!.MatchId.Should().Be(request.Id);
        launch.Match.Status.Should().Be(MatchStatus.Scheduled);
        launch.Match.StreamUrl.Should().Be($"/api/matches/{request.Id}/stream");
        FanMatchIds.IsFanMatch(request.Id).Should().BeTrue();
        CosmosIds.IsValid(request.Id).Should().BeTrue();
        request.Seed.Should().Be(77);
        request.Status.Should().Be(FanMatchStatus.Scheduled);
        request.KickOffUtc.Should().Be(_time.GetUtcNow());
        request.Home.Should().BeEquivalentTo(new FanMatchSide(_fanId, _fan, _sheet), "the fan team is frozen with its sheet and bench");
        request.Away.Should().Be(new FanMatchSide(_builtInId, null, null), "a built-in team comes from the catalogue");
        _job.Verify(j => j.StartAsync(request.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Without_a_seed_each_request_gets_its_own()
    {
        var seeds = new HashSet<int>();
        for (var i = 0; i < 5; i++)
        {
            await Launcher().LaunchAsync(new MatchRequest(_fanId, _builtInId), null, CancellationToken.None);
            seeds.Add(_written!.Seed);
        }

        seeds.Should().HaveCountGreaterThan(1);
    }

    [Test]
    public async Task Chaos_a_job_that_will_not_start_marks_the_request_failed_and_answers_unavailable()
    {
        _job.Setup(j => j.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("start"))
            .ThrowsAsync(new Azure.RequestFailedException(403, "AuthorizationFailed"));

        var launch = await Launcher().LaunchAsync(new MatchRequest(_fanId, _builtInId), null, CancellationToken.None);

        launch.Outcome.Should().Be(LaunchOutcome.Unavailable);
        launch.Reason.Should().Be(FanMatchLauncher.StartFailedReason);
        _calls.Should().Equal("create", "start", "status");
        _requests.Verify(r => r.SetStatusAsync(
            _written!.Id, FanMatchStatus.Failed, FanMatchLauncher.JobFailedReason,
            It.Is<IReadOnlyCollection<FanMatchStatus>>(from => from.SequenceEqual(new[] { FanMatchStatus.Scheduled })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Chaos_a_request_store_that_is_down_answers_unavailable_and_never_starts_the_job()
    {
        _requests.Setup(r => r.CreateAsync(It.IsAny<FanMatchRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("down"));

        var launch = await Launcher().LaunchAsync(new MatchRequest(_fanId, _builtInId), null, CancellationToken.None);

        launch.Outcome.Should().Be(LaunchOutcome.Unavailable);
        _job.Verify(j => j.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task A_team_is_playing_while_a_recent_request_naming_it_is_active()
    {
        var active = Request("exh-202610241500-0000000a", FanMatchStatus.Running);
        _requests.Setup(r => r.ListActiveAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync([active]);

        (await Launcher().IsTeamPlayingAsync(_fanId, CancellationToken.None)).Should().BeTrue();
        (await Launcher().IsTeamPlayingAsync(_builtInId, CancellationToken.None)).Should().BeTrue();
        (await Launcher().IsTeamPlayingAsync("fan-someone-else", CancellationToken.None)).Should().BeFalse();
        _requests.Verify(r => r.ListActiveAsync(_time.GetUtcNow() - new FanMatchOptions().ActiveFor, It.IsAny<CancellationToken>()));
    }

    [Test]
    public void The_execution_template_is_the_job_s_template_with_one_variable_added()
    {
        var job = new ContainerAppJobTemplate();
        var simulator = new ContainerAppContainer
        {
            Name = "simulator",
            Image = "crtouchline.azurecr.io/simulator:abc123",
            Resources = new AppContainerResources { Cpu = 0.25, Memory = "0.5Gi" },
        };
        simulator.Env.Add(new ContainerAppEnvironmentVariable { Name = "APPLICATIONINSIGHTS_CONNECTION_STRING", SecretRef = "appinsights-connection-string" });
        simulator.Env.Add(new ContainerAppEnvironmentVariable { Name = "Simulator__ExitWhenDone", Value = "true" });
        simulator.Command.Add("dotnet");
        simulator.Args.Add("Touchline.Simulator.dll");
        job.Containers.Add(simulator);

        var execution = SimulatorJobTemplates.ForRequest(job, "exh-202610241530-0000000b");

        var container = execution.Containers.Should().ContainSingle().Subject;
        container.Name.Should().Be("simulator");
        container.Image.Should().Be(simulator.Image);
        container.Resources.Cpu.Should().Be(0.25);
        container.Resources.Memory.Should().Be("0.5Gi");
        container.Command.Should().Equal("dotnet");
        container.Args.Should().Equal("Touchline.Simulator.dll");
        container.Env.Select(e => (e.Name, (string?)e.Value, (string?)e.SecretRef)).Should().Equal(
            ("APPLICATIONINSIGHTS_CONNECTION_STRING", null, "appinsights-connection-string"),
            ("Simulator__ExitWhenDone", "true", null),
            (SimulatorJobTemplates.RequestIdVariable, "exh-202610241530-0000000b", null));
        simulator.Env.Should().HaveCount(2, "the job's own template is never changed");
    }

    [Test]
    public void A_job_with_no_containers_is_refused()
    {
        var act = () => SimulatorJobTemplates.ForRequest(new ContainerAppJobTemplate(), "exh-202610241530-0000000b");

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void A_request_round_trips_through_DomainJson_with_its_snapshot()
    {
        var request = Request("exh-202610241530-0000000c", FanMatchStatus.Scheduled);

        var read = JsonSerializer.Deserialize<FanMatchRequest>(JsonSerializer.Serialize(request, DomainJson.Options), DomainJson.Options);

        read.Should().BeEquivalentTo(request);
        JsonSerializer.Serialize(request, DomainJson.Options).Should().Contain("\"id\":\"exh-202610241530-0000000c\"").And.Contain("\"status\":\"Scheduled\"");
    }

    [TestCase("exh-202610241530-0a1b2c3d", true)]
    [TestCase("exh-202610241530-0A1B2C3D", false)]
    [TestCase("exh-2026102415-0a1b2c3d", false)]
    [TestCase("north-bank-fc-thames-valley-united-42", false)]
    [TestCase(null, false)]
    public void Fan_match_ids_have_one_shape(string? id, bool isFanMatch) =>
        FanMatchIds.IsFanMatch(id).Should().Be(isFanMatch);

    [Test]
    public void A_new_id_is_a_fan_match_id_safe_for_Cosmos_DB()
    {
        var id = FanMatchIds.New(_time.GetUtcNow());

        id.Should().StartWith("exh-202610241530-");
        FanMatchIds.IsFanMatch(id).Should().BeTrue();
        CosmosIds.IsValid(id).Should().BeTrue();
    }

    private static FanMatchRequest Request(string id, FanMatchStatus status) => new(
        id,
        new DateTimeOffset(2026, 10, 24, 15, 0, 0, TimeSpan.Zero),
        5,
        new DateTimeOffset(2026, 10, 24, 15, 0, 0, TimeSpan.Zero),
        new FanMatchSide(_fanId, _fan, _sheet),
        new FanMatchSide(_builtInId, null, null),
        status);

    /// <summary>The Match Centre's stored view of a fan team, as the team builder writes it.</summary>
    internal static TeamDetail Detail(FanTeamSpec spec) => new(
        new TeamSummary(spec.Id, spec.Name, spec.ShortName, spec.Venue, spec.Colours, TeamOrigin.Fan, TeamViews.CrestUrl(spec.Id)),
        new ManagerSpec(spec.Manager.Name, spec.Manager.Style),
        new CrestSpec(CrestShape.Shield, CrestEmblem.Ball, "TR", spec.Colours.Primary, spec.Colours.Secondary),
        [.. spec.Players.Select(p => new PlayerSummary(p.Id, p.Name, p.ShirtNumber, Archetypes.PositionOf(p.Archetype), spec.Id, p.Archetype, p.PreferredFoot))],
        1);

    private FanMatchLauncher Launcher() => new(
        _teams.Object, _requests.Object, _limits.Object, _job.Object, _time, Options.Create(new FanMatchOptions()), NullLogger<FanMatchLauncher>.Instance);
}
