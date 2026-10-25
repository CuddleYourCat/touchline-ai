extern alias processor;
extern alias simulator;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using processor::Touchline.EventProcessor.Processing;
using simulator::Touchline.Simulator.Publishing;
using Touchline.Agents;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;
using Touchline.Search;
using Touchline.Storage;

namespace Touchline.Tests.Unit.EventProcessor;

/// <summary>
/// A fan match's players are named from its request's snapshot everywhere names are resolved
/// (ADR-050): key-moment reasons, the roster guardrail, and the possession indexer's roster.
/// </summary>
[TestFixture]
public class FanRosterTests
{
    private const string _matchId = "exh-202610241530-5e5e5e5e";
    private static readonly FanTeamSpec _fan = TestFanTeams.Standard("fan-test-rovers", ManagerStyle.HighPress);

    private static readonly FanMatchRequest _request = new(
        _matchId,
        new DateTimeOffset(2026, 10, 24, 15, 30, 0, TimeSpan.Zero),
        Seed: 31,
        KickOffUtc: new DateTimeOffset(2026, 10, 24, 15, 30, 0, TimeSpan.Zero),
        new FanMatchSide(_fan.Id, _fan, null),
        new FanMatchSide("north-bank-fc", null, null),
        FanMatchStatus.Scheduled);

    private Mock<IFanMatchRequestStore> _requests = null!;
    private FanMatchTeamProfiles _profiles = null!;

    [SetUp]
    public void CreateLookup()
    {
        _requests = new Mock<IFanMatchRequestStore>();
        _requests.Setup(r => r.GetAsync(_matchId, It.IsAny<CancellationToken>())).ReturnsAsync(_request);
        _profiles = new FanMatchTeamProfiles(_requests.Object, NullLogger<FanMatchTeamProfiles>.Instance);
    }

    [Test]
    public async Task A_fan_match_s_key_moment_reasons_name_its_fan_players()
    {
        var (_, moments) = await ProcessAsync(_profiles);

        var fanMoments = moments.Where(m => m.Trigger.PlayerId?.StartsWith(_fan.Id, StringComparison.Ordinal) == true).ToList();
        fanMoments.Should().NotBeEmpty("seed 31 gives the fan side at least one key moment");
        foreach (var moment in fanMoments)
        {
            var name = _fan.Players.Single(p => p.Id == moment.Trigger.PlayerId).Name;
            moment.Reason.Should().Contain($"{name} ({_fan.ShortName})").And.NotContain(moment.Trigger.PlayerId!);
        }
    }

    [Test]
    public async Task Without_the_lookup_a_fan_player_is_named_by_id_which_is_why_the_lookup_exists()
    {
        var (_, moments) = await ProcessAsync(teams: null);

        moments.Where(m => m.Trigger.PlayerId?.StartsWith(_fan.Id, StringComparison.Ordinal) == true)
            .Should().OnlyContain(m => m.Reason.Contains(m.Trigger.PlayerId!));
    }

    [Test]
    public async Task The_guardrail_allows_the_fan_team_s_names_for_its_match_and_still_rejects_anyone_else()
    {
        var (match, moments) = await ProcessAsync(_profiles);
        var profiles = await _profiles.ForMatchAsync(_matchId, CancellationToken.None);
        var context = MatchContexts.For(moments[0], match, profiles);
        var fanName = _fan.Players[9].Name;
        var builtInName = TeamProfileCatalog.LoadEmbedded()["north-bank-fc"].Squad[0].Name;

        RosterGuardrail.FindUnknownNames($"{fanName} races clear for {_fan.Name}!", context.RosterNames, context.OtherKnownNames).Should().BeEmpty();
        RosterGuardrail.FindUnknownNames($"{builtInName} slides in.", context.RosterNames, context.OtherKnownNames).Should().BeEmpty();
        RosterGuardrail.FindUnknownNames("Imaginary Striker scores!", context.RosterNames, context.OtherKnownNames)
            .Should().Equal("Imaginary Striker");

        var withoutLookup = MatchContexts.For(moments[0], match);
        RosterGuardrail.FindUnknownNames($"{fanName} races clear.", withoutLookup.RosterNames, withoutLookup.OtherKnownNames)
            .Should().NotBeEmpty("without the snapshot the fan player is unknown, and the template line is used");
    }

    [Test]
    public async Task The_possession_indexer_s_roster_names_the_fan_players()
    {
        var (match, _) = await ProcessAsync(_profiles);

        var roster = PossessionRoster.From(match.Metadata, await _profiles.ForMatchAsync(_matchId, CancellationToken.None));

        roster.NameOf(_fan.Players[9].Id).Should().Be(_fan.Players[9].Name);
        roster.HomeName.Should().Be(_fan.Name);
        PossessionRoster.From(match.Metadata).NameOf(_fan.Players[9].Id).Should().BeNull("the catalogue alone doesn't know the fan team");
    }

    [Test]
    public async Task A_fan_match_s_profiles_are_read_once_and_a_built_in_match_needs_no_read()
    {
        var first = await _profiles.ForMatchAsync(_matchId, CancellationToken.None);
        var second = await _profiles.ForMatchAsync(_matchId, CancellationToken.None);
        var builtIn = await _profiles.ForMatchAsync("north-bank-fc-thames-valley-united-42", CancellationToken.None);

        first.Should().ContainKey(_fan.Id).And.ContainKey("north-bank-fc");
        second.Should().BeSameAs(first);
        builtIn.Should().BeNull();
        _requests.Verify(r => r.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Chaos_a_request_store_outage_degrades_to_the_catalogue_and_is_not_cached()
    {
        _requests.SetupSequence(r => r.GetAsync(_matchId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CosmosException("unavailable", System.Net.HttpStatusCode.ServiceUnavailable, 0, "a", 0))
            .ReturnsAsync(_request);

        var during = await _profiles.ForMatchAsync(_matchId, CancellationToken.None);
        var after = await _profiles.ForMatchAsync(_matchId, CancellationToken.None);

        during.Should().BeNull("the match degrades to unnamed fan players and template lines");
        after.Should().ContainKey(_fan.Id, "a recovered store is used at once");
    }

    [Test]
    public async Task A_missing_request_degrades_to_the_catalogue()
    {
        _requests.Setup(r => r.GetAsync(_matchId, It.IsAny<CancellationToken>())).ReturnsAsync((FanMatchRequest?)null);

        (await _profiles.ForMatchAsync(_matchId, CancellationToken.None)).Should().BeNull();
    }

    [Test]
    public void The_notable_detector_names_fan_players_through_its_match_teams()
    {
        var profiles = _request.TeamsWith(TeamProfileCatalog.LoadEmbedded());
        var notable = new NotableMomentDetector(new KeyMomentDetector()).WithTeams(profiles.Values);
        var events = FanMatchPlans.CreateSimulator(_request).Play().ToList();

        var state = MatchState.KickOff(_matchId);
        var named = new List<NotableMoment>();
        foreach (var e in events)
        {
            if (notable.Detect(e, state) is { } moment && e.PlayerId?.StartsWith(_fan.Id, StringComparison.Ordinal) == true)
            {
                named.Add(moment);
            }

            state = state.Apply(e);
        }

        named.Should().NotBeEmpty().And.OnlyContain(m => _fan.Players.Any(p => p.Name == m.PlayerName));
    }

    private static async Task<(MatchRecord Match, List<KeyMoment> Moments)> ProcessAsync(IMatchTeamProfiles? teams)
    {
        var events = FanMatchPlans.CreateSimulator(_request).Play().ToList();
        var store = new InMemoryMatchStore();
        var publisher = new CapturingPublisher();
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var processor = new MatchEventProcessor(
            store, publisher, new KeyMomentDetector(), new PipelineTelemetry(services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()),
            NullLogger<MatchEventProcessor>.Instance, teams);

        await processor.ProcessAsync(
            [.. events.Select(e => new IncomingEvent(EventEnvelope.For(e), BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(e, DomainJson.Options))))],
            CancellationToken.None);

        return (store.Matches[_matchId], publisher.Published);
    }

    private sealed class CapturingPublisher : IKeyMomentPublisher
    {
        public List<KeyMoment> Published { get; } = [];

        public Task PublishAsync(IReadOnlyList<KeyMoment> moments, CancellationToken cancellationToken)
        {
            Published.AddRange(moments);
            return Task.CompletedTask;
        }
    }
}
