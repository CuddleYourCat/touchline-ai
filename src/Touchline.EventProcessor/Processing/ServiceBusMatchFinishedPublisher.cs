using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Touchline.Domain;

namespace Touchline.EventProcessor.Processing;

/// <summary>
/// Publishes "match finished" to the Service Bus topic <c>match-finished</c> (ADR-030, amended
/// 23 Oct), which the recap function's <c>recaps</c> subscription reads.
/// </summary>
/// <param name="client">The Service Bus client, configured by Aspire.</param>
public sealed class ServiceBusMatchFinishedPublisher(ServiceBusClient client) : IMatchFinishedPublisher, IAsyncDisposable
{
    /// <summary>The topic name.</summary>
    public const string TopicName = "match-finished";

    private readonly ServiceBusSender _sender = client.CreateSender(TopicName);

    /// <inheritdoc />
    public Task PublishAsync(string matchId, CancellationToken cancellationToken) =>
        _sender.SendMessageAsync(ToMessage(matchId), cancellationToken);

    /// <summary>
    /// The message for a finished match: its body is <see cref="MatchFinished"/>, and its message ID
    /// is <c>{matchId}:finished</c>, so a republish is dropped by duplicate detection.
    /// </summary>
    /// <param name="matchId">The match.</param>
    /// <returns>The message.</returns>
    public static ServiceBusMessage ToMessage(string matchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matchId);
        return new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(new MatchFinished(matchId), DomainJson.Options))
        {
            MessageId = MessageIdFor(matchId),
            ContentType = "application/json",
            ApplicationProperties = { ["matchId"] = matchId }
        };
    }

    /// <summary>The message ID for a finished match.</summary>
    /// <param name="matchId">The match.</param>
    /// <returns><c>{matchId}:finished</c>.</returns>
    public static string MessageIdFor(string matchId) => $"{matchId}:finished";

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _sender.DisposeAsync();
}

/// <summary>The body of a "match finished" message.</summary>
/// <param name="MatchId">The match that reached full time.</param>
public sealed record MatchFinished(string MatchId);
