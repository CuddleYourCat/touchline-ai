extern alias processor;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
using processor::Touchline.EventProcessor;
using processor::Touchline.EventProcessor.Processing;
using Touchline.Domain;
using Touchline.Domain.Matches;

namespace Touchline.Tests.Unit.EventProcessor;

/// <summary>The change feed as the outbox for "match finished" (ADR-030, amended 23 Oct).</summary>
[TestFixture]
public class AnnounceFinishedMatchesTests
{
    private RecordingFinishedPublisher _publisher = null!;
    private AnnounceFinishedMatches _function = null!;

    [SetUp]
    public void SetUp()
    {
        _publisher = new RecordingFinishedPublisher();
        _function = new AnnounceFinishedMatches(_publisher);
    }

    [Test]
    public async Task A_full_time_document_is_announced_and_one_still_playing_is_not()
    {
        await _function.RunAsync(Documents(Match("m-1", fullTime: true), Match("m-2", fullTime: false)), CancellationToken.None);

        _publisher.Published.Should().Equal("m-1");
    }

    [Test]
    public async Task A_repeated_document_is_published_with_the_same_message_id()
    {
        await _function.RunAsync(Documents(Match("m-1", fullTime: true), Match("m-1", fullTime: true)), CancellationToken.None);

        _publisher.Published.Select(id => ServiceBusMatchFinishedPublisher.ToMessage(id).MessageId)
            .Should().Equal("m-1:finished", "m-1:finished");
    }

    [Test]
    public async Task A_failed_publish_throws_so_the_lease_holds_and_the_retry_succeeds()
    {
        _publisher.FailNext = true;
        var batch = Documents(Match("m-1", fullTime: true));

        var first = () => _function.RunAsync(batch, CancellationToken.None);
        await first.Should().ThrowAsync<InvalidOperationException>();
        await _function.RunAsync(batch, CancellationToken.None);

        _publisher.Published.Should().Equal("m-1");
    }

    [Test]
    public void The_trigger_shares_the_lease_container_with_its_own_non_empty_prefix()
    {
        var announce = Trigger(typeof(AnnounceFinishedMatches));
        var project = Trigger(typeof(ProjectSeason));

        announce.LeaseContainerName.Should().NotBeNullOrWhiteSpace().And.Be(project.LeaseContainerName);
        announce.LeaseContainerPrefix.Should().NotBeNullOrWhiteSpace().And.Be(AnnounceFinishedMatches.LeaseContainerPrefix);
        (project.LeaseContainerPrefix ?? string.Empty).Should().NotBe(announce.LeaseContainerPrefix);
    }

    [Test]
    public void The_announcements_retry_indefinitely_so_the_lease_never_checkpoints_past_a_failure()
    {
        var retry = typeof(AnnounceFinishedMatches).GetMethod(nameof(AnnounceFinishedMatches.RunAsync))!
            .GetCustomAttribute<ExponentialBackoffRetryAttribute>()!;

        retry.MaxRetryCount.Should().Be(-1);
    }

    private static CosmosDBTriggerAttribute Trigger(Type function) =>
        function.GetMethod("RunAsync")!.GetParameters()
            .Select(p => p.GetCustomAttribute<CosmosDBTriggerAttribute>())
            .Single(a => a is not null)!;

    private static MatchRecord Match(string matchId, bool fullTime)
    {
        var record = MatchRecord.New(matchId);
        return record with { State = record.State with { FullTime = fullTime } };
    }

    private static string Documents(params MatchRecord[] records) => JsonSerializer.Serialize(records, DomainJson.Options);

    private sealed class RecordingFinishedPublisher : IMatchFinishedPublisher
    {
        public List<string> Published { get; } = [];

        public bool FailNext { get; set; }

        public Task PublishAsync(string matchId, CancellationToken cancellationToken)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("Service Bus is unavailable.");
            }

            Published.Add(matchId);
            return Task.CompletedTask;
        }
    }
}
