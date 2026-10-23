using System.Diagnostics;
using System.Diagnostics.Metrics;
using Touchline.Domain.Matches;

namespace Touchline.EventProcessor.Processing;

/// <summary>
/// The Event Processor's own traces and metrics (ADR-023). Their source and meter are named
/// after the application, which the service defaults export over OpenTelemetry.
/// </summary>
public sealed class PipelineTelemetry : IDisposable
{
    /// <summary>The name of the activity source and the meter.</summary>
    public const string Name = "Touchline.EventProcessor";

    private readonly Meter _meter;
    private readonly Counter<long> _processed;
    private readonly Counter<long> _skipped;
    private readonly Counter<long> _duplicates;
    private readonly Counter<long> _keyMoments;
    private readonly Counter<long> _lost;
    private readonly Counter<long> _missingMetadata;
    private readonly Counter<long> _fixturesProjected;
    private readonly Counter<long> _commentarySkipped;
    private readonly Counter<long> _commentaryDeadLettered;

    /// <summary>
    /// Creates the meter and its counters.
    /// </summary>
    /// <param name="meterFactory">The host's meter factory, so the meter's lifetime follows the host's.</param>
    public PipelineTelemetry(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        _meter = meterFactory.Create(Name);
        _processed = _meter.CreateCounter<long>("touchline.events.processed", "{event}", "Events applied to match state and stored.");
        _skipped = _meter.CreateCounter<long>("touchline.events.skipped", "{event}", "Events a tolerant reader skipped, tagged with the reason.");
        _duplicates = _meter.CreateCounter<long>("touchline.events.duplicates", "{event}", "Events already processed, seen again because delivery is at least once.");
        _keyMoments = _meter.CreateCounter<long>("touchline.key_moments.published", "{moment}", "Key moments published to the key-moments topic.");
        _lost = _meter.CreateCounter<long>("touchline.events.lost", "{event}", "Events never received, found as a gap in a match's sequence. Alert on any.");
        _missingMetadata = _meter.CreateCounter<long>("touchline.matches.missing_metadata", "{match}", "Matches whose first event (the metadata) was skipped or lost.");
        _fixturesProjected = _meter.CreateCounter<long>("touchline.season.fixtures_projected", "{fixture}", "Finished fixtures projected into player and season documents (ADR-025).");
        _commentarySkipped = _meter.CreateCounter<long>("touchline.commentary.skipped", "{moment}", "Redelivered key moments whose English model lines were already stored, so the squad was not run again.");
        _commentaryDeadLettered = _meter.CreateCounter<long>("touchline.commentary.dead_lettered", "{message}", "Key-moment messages that could not be read and went to the dead-letter queue. Alert on any.");
    }

    /// <summary>The activity source for the processor's spans.</summary>
    public static ActivitySource Source { get; } = new(Name);

    /// <summary>Counts events applied and stored.</summary>
    /// <param name="count">How many.</param>
    public void Processed(int count) => _processed.Add(count);

    /// <summary>Counts an event a tolerant reader skipped.</summary>
    /// <param name="reason">Why it was skipped.</param>
    public void Skipped(SkipReason reason) => _skipped.Add(1, new KeyValuePair<string, object?>("reason", reason.ToString()));

    /// <summary>Counts an event seen again after it had already been processed.</summary>
    public void Duplicate() => _duplicates.Add(1);

    /// <summary>Counts key moments published.</summary>
    /// <param name="count">How many.</param>
    public void KeyMomentsPublished(int count) => _keyMoments.Add(count);

    /// <summary>Counts events lost upstream, found as a gap in a match's sequence.</summary>
    /// <param name="count">How many sequences were missing.</param>
    public void Lost(long count) => _lost.Add(count);

    /// <summary>Counts a match processed without its metadata.</summary>
    public void MissingMetadata() => _missingMetadata.Add(1);

    /// <summary>Counts a finished fixture projected into the season.</summary>
    public void FixtureProjected() => _fixturesProjected.Add(1);

    /// <summary>Counts a redelivered key moment whose commentary the models had already written.</summary>
    public void CommentarySkipped() => _commentarySkipped.Add(1);

    /// <summary>Counts a key-moment message sent to the dead-letter queue.</summary>
    public void CommentaryDeadLettered() => _commentaryDeadLettered.Add(1);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
