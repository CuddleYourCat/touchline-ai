extern alias processor;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using processor::Touchline.EventProcessor;
using processor::Touchline.EventProcessor.Processing;
using Touchline.Agents;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Pitch;
using Touchline.Domain.Squad;
using Touchline.Domain.Teams;
using Touchline.Domain.Viewing;
using Touchline.Storage;
using Touchline.Tests.Unit.Agents;

namespace Touchline.Tests.Unit.EventProcessor;

/// <summary>
/// The live commentary host (ADR-030): the Service Bus trigger with a fake chat client, an
/// in-memory match store and a mocked commentary store and message settlement. No live services.
/// </summary>
[TestFixture]
public class CommentaryFunctionTests
{
    private const string _matchId = "demo-1";

    private static readonly IReadOnlyDictionary<string, TeamProfile> _teams = TeamProfileCatalog.LoadEmbedded();
    private static readonly TeamProfile _home = _teams["north-bank-fc"];
    private static readonly TeamProfile _away = _teams["thames-valley-united"];

    private ServiceProvider _provider = null!;
    private FakeChatClient _chat = null!;
    private List<CommentaryLine> _saved = null!;
    private List<CommentaryLine> _existing = null!;
    private Mock<ICommentaryStore> _commentary = null!;
    private InMemoryMatchStore _matches = null!;
    private Mock<ServiceBusMessageActions> _actions = null!;

    [SetUp]
    public void SetUp()
    {
        _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        _chat = new FakeChatClient();
        _saved = [];
        _existing = [];
        _commentary = new Mock<ICommentaryStore>();
        _commentary.Setup(s => s.SaveAsync(It.IsAny<CommentaryLine>(), It.IsAny<CancellationToken>()))
            .Callback<CommentaryLine, CancellationToken>((line, _) => _saved.Add(line))
            .Returns(Task.CompletedTask);
        _commentary.Setup(s => s.FindAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ViewingMode>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string momentId, ViewingMode mode, string language, CancellationToken _) =>
                _existing.LastOrDefault(line => line.MomentId == momentId && line.Mode == mode && line.Language == language));
        _matches = new InMemoryMatchStore();
        _matches.Matches[_matchId] = Record();
        _actions = new Mock<ServiceBusMessageActions>();
    }

    [TearDown]
    public void TearDown()
    {
        _chat.Dispose();
        _provider.Dispose();
    }

    private static Player Scorer => _home.Squad[9];

    private static MatchRecord Record(TeamSheet? away = null) =>
        MatchRecord.New(_matchId) with
        {
            Metadata = new MatchMetadata(
                Sheet(_home),
                away ?? Sheet(_away),
                _home.Venue,
                new DateTimeOffset(2026, 10, 24, 15, 0, 0, TimeSpan.Zero),
                42,
                new RefereeSheet("ref-1", "Dana Whitfield"),
                MatchKind.Exhibition)
        };

    private static TeamSheet Sheet(TeamProfile team) =>
        new(team.Id, team.Name, team.ShortName, [.. team.Squad.Take(11).Select(p => p.Id)]);

    private static KeyMoment Goal(int? homeScore = 1, int? awayScore = 0) =>
        new(
            KeyMomentKind.Goal,
            new MatchEvent
            {
                Sequence = 412,
                MatchId = _matchId,
                PossessionId = 31,
                Time = MatchTime.At(MatchPeriod.FirstHalf, 23),
                Type = MatchEventType.Pass,
                Team = TeamSide.Home,
                PlayerId = Scorer.Id,
                Start = new PitchPosition(100, 40)
            },
            "Goal: the home side lead 1-0 after 23 minutes.",
            HomeScore: homeScore,
            AwayScore: awayScore);

    private static ServiceBusReceivedMessage Message(KeyMoment moment) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: ServiceBusKeyMomentPublisher.ToMessage(moment).Body,
            messageId: $"{moment.Trigger.MatchId}:{moment.Trigger.Sequence}");

    private string CleanLines() => JsonSerializer.Serialize(new
    {
        casual = $"{Scorer.Name} scores for {_home.Name}!",
        analyst = $"{Scorer.Name} finishes a fine chance."
    });

    private CommentaryFunction Function(IServiceProvider? services = null)
    {
        var meters = _provider.GetRequiredService<IMeterFactory>();
        var workflow = services?.GetRequiredService<CommentaryWorkflow>() ?? new CommentaryWorkflow(
            _chat,
            _commentary.Object,
            Options.Create(new SquadOptions { Languages = ["en"] }),
            new SquadMetrics(meters),
            NullLogger<CommentaryWorkflow>.Instance);
        return new CommentaryFunction(workflow, _commentary.Object, _matches, new PipelineTelemetry(meters), NullLogger<CommentaryFunction>.Instance);
    }

    private IEnumerable<string> Settlements() => _actions.Invocations.Select(i => i.Method.Name);

    [Test]
    public void The_context_names_both_whole_squads_the_teams_venue_and_referee_with_the_published_score()
    {
        var context = MatchContexts.For(Goal(2, 1), Record());

        context.MatchId.Should().Be(_matchId);
        context.HomePlayers.Should().Equal(_home.Squad.Select(p => p.Name));
        context.AwayPlayers.Should().Equal(_away.Squad.Select(p => p.Name));
        context.HomePlayers.Should().Contain(_home.Squad[^1].Name, "a substitute who comes on must pass the guardrail");
        context.OtherKnownNames.Should().BeEquivalentTo(
            new[] { _home.Name, _home.ShortName, _away.Name, _away.ShortName, _home.Venue, "Dana Whitfield" }.Distinct());
        (context.HomeScore, context.AwayScore).Should().Be((2, 1));
    }

    [Test]
    public void A_team_the_catalogue_does_not_know_has_an_empty_roster_logged_once()
    {
        var teamId = $"fan-{Guid.NewGuid():N}";
        string[] ids = [.. Enumerable.Range(1, 11).Select(n => $"{teamId}-p{n}")];
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<Microsoft.Extensions.Logging.LogLevel>())).Returns(true);
        var record = Record(new TeamSheet(teamId, "Moor End Rovers", "MER", ids));

        var context = MatchContexts.For(Goal(), record, logger: logger.Object);
        MatchContexts.For(Goal(), record, logger: logger.Object);

        context.AwayPlayers.Should().BeEmpty("raw player IDs must never reach a model");
        context.HomePlayers.Should().NotBeEmpty();
        context.OtherKnownNames.Should().Contain("Moor End Rovers");
        logger.Invocations.Count(i => i.Method.Name == nameof(Microsoft.Extensions.Logging.ILogger.Log)).Should().Be(1);
    }

    [Test]
    public void A_message_without_the_score_has_an_unknown_score_not_the_later_stored_state()
    {
        var record = Record() with { State = MatchState.KickOff(_matchId) with { Home = TeamStats.Empty with { Goals = 3 } } };

        var context = MatchContexts.For(Goal(null, null), record);

        (context.HomeScore, context.AwayScore).Should().Be((null, null));
        context.HasScore.Should().BeFalse();
    }

    [Test]
    public async Task A_key_moment_runs_the_workflow_with_the_match_rosters_and_completes_the_message()
    {
        _chat.Returns(CleanLines());

        await Function().RunAsync(Message(Goal()), _actions.Object, CancellationToken.None);

        _saved.Should().HaveCount(2).And.OnlyContain(line =>
            line.Source == CommentarySource.Model && line.MomentId == $"{_matchId}:412" && line.Language == "en");
        _chat.Requests.Should().ContainSingle()
            .Which.Messages.Select(m => m.Text).Should().Contain(text => text.Contains(_away.Squad[0].Name, StringComparison.Ordinal));
        Settlements().Should().Equal(nameof(ServiceBusMessageActions.CompleteMessageAsync));
    }

    [Test]
    public async Task A_redelivered_moment_with_english_model_lines_for_both_modes_is_completed_without_running_the_models()
    {
        _existing.Add(Stored(CommentarySource.Model, ViewingMode.Casual));
        _existing.Add(Stored(CommentarySource.Model, ViewingMode.Analyst));

        await Function().RunAsync(Message(Goal()), _actions.Object, CancellationToken.None);

        _chat.Requests.Should().BeEmpty("a redelivery mustn't pay for the models twice");
        _saved.Should().BeEmpty();
        Settlements().Should().Equal(nameof(ServiceBusMessageActions.CompleteMessageAsync));
        _commentary.Verify(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never, "the skip check uses point reads");
    }

    [Test]
    public async Task An_english_model_line_for_only_one_mode_does_not_skip_the_run()
    {
        _existing.Add(Stored(CommentarySource.Model, ViewingMode.Casual));
        _chat.Returns(CleanLines());

        await Function().RunAsync(Message(Goal()), _actions.Object, CancellationToken.None);

        _chat.Requests.Should().ContainSingle();
        _saved.Should().HaveCount(2);
    }

    [Test]
    public async Task An_english_model_line_that_fails_to_save_throws_so_the_message_is_redelivered()
    {
        _chat.Returns(CleanLines());
        _commentary.Setup(s => s.SaveAsync(It.Is<CommentaryLine>(l => l.Source == CommentarySource.Model), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Cosmos DB is struggling."));

        var run = async () => await Function().RunAsync(Message(Goal()), _actions.Object, CancellationToken.None);

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*412*redelivered*");
        _actions.Invocations.Should().BeEmpty();
        _saved.Should().OnlyContain(line => line.Source == CommentarySource.Template, "the template stands in until the retry");
    }

    [Test]
    public async Task A_template_line_that_fails_to_save_still_completes()
    {
        _chat.Throws(new HttpRequestException("The model is unavailable."));
        _commentary.Setup(s => s.SaveAsync(It.IsAny<CommentaryLine>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Cosmos DB is struggling."));

        await Function().RunAsync(Message(Goal()), _actions.Object, CancellationToken.None);

        Settlements().Should().Equal(nameof(ServiceBusMessageActions.CompleteMessageAsync));
    }

    private static CommentaryLine Stored(CommentarySource source, ViewingMode mode) =>
        new(_matchId, $"{_matchId}:412", 412, MomentTier.Key, source, "Already said.", mode, "en");

    [Test]
    public async Task A_stored_template_line_alone_does_not_skip_the_run()
    {
        _existing.Add(new CommentaryLine(_matchId, $"{_matchId}:412", 412, MomentTier.Key, CommentarySource.Template, "Goal.", ViewingMode.Casual, "en"));
        _chat.Returns(CleanLines());

        await Function().RunAsync(Message(Goal()), _actions.Object, CancellationToken.None);

        _chat.Requests.Should().ContainSingle();
    }

    [TestCase("not json at all")]
    [TestCase("""{"kind":"Goal","reason":"no trigger"}""")]
    [TestCase("null")]
    public async Task An_unreadable_message_is_dead_lettered_with_its_reason(string body)
    {
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString(body), messageId: "bad-1");

        await Function().RunAsync(message, _actions.Object, CancellationToken.None);

        var deadLetter = _actions.Invocations.Should().ContainSingle().Which;
        deadLetter.Method.Name.Should().Be(nameof(ServiceBusMessageActions.DeadLetterMessageAsync));
        deadLetter.Arguments.Should().Contain(CommentaryFunction.UnreadableReason);
        _chat.Requests.Should().BeEmpty();
        _commentary.Verify(s => s.FindAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ViewingMode>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Cosmos_db_being_unavailable_throws_so_the_message_is_redelivered_not_settled()
    {
        _commentary.Setup(s => s.FindAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ViewingMode>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CosmosException("Service unavailable", System.Net.HttpStatusCode.ServiceUnavailable, 0, "", 0));

        var run = async () => await Function().RunAsync(Message(Goal()), _actions.Object, CancellationToken.None);

        await run.Should().ThrowAsync<CosmosException>();
        _actions.Invocations.Should().BeEmpty();
    }

    [Test]
    public async Task Without_foundry_the_host_builds_starts_and_a_moment_stores_english_template_lines()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Squad:Languages:0"] = "en", ["Squad:Languages:1"] = "es" });
        builder.Services.AddSingleton(_commentary.Object);
        builder.Services.AddLiveCommentary(builder.Configuration);
        using var host = builder.Build();

        await host.StartAsync();
        await Function(host.Services).RunAsync(Message(Goal()), _actions.Object, CancellationToken.None);
        await host.StopAsync();

        host.Services.GetRequiredKeyedService<IChatClient>(SquadModels.Large).Should().BeOfType<UnavailableChatClient>();
        _saved.Should().NotBeEmpty().And.OnlyContain(line => line.Source == CommentarySource.Template && line.Language == "en");
        Settlements().Should().Equal(nameof(ServiceBusMessageActions.CompleteMessageAsync));
    }

    [Test]
    public void Without_foundry_the_real_registrations_resolve_with_a_cosmos_client()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(new Mock<CosmosClient>().Object);
        services.AddLiveCommentary(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetRequiredService<ICommentaryStore>().Should().BeOfType<CosmosCommentaryStore>();
        provider.GetRequiredService<CommentaryWorkflow>().Should().NotBeNull();
        provider.GetRequiredService<IOptions<SquadOptions>>().Value.Languages.Should().Equal(SquadOptions.DefaultLanguages);
        provider.GetRequiredService<IOptions<SquadOptions>>().Value.ModelsAvailable.Should().BeFalse();
    }

    [Test]
    public void With_foundry_configured_the_real_model_clients_are_registered()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Squad:FoundryEndpoint"] = "https://example.services.ai.azure.com/",
                ["Squad:Deployments:Small"] = "small-model",
                ["Squad:Deployments:Large"] = "large-model"
            })
            .Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(new Mock<CosmosClient>().Object);
        services.AddLiveCommentary(configuration);
        using var provider = services.BuildServiceProvider();

        LiveCommentaryServiceCollectionExtensions.IsFoundryConfigured(configuration).Should().BeTrue();
        provider.GetRequiredKeyedService<IChatClient>(SquadModels.Small).Should().NotBeOfType<UnavailableChatClient>();
    }
}
