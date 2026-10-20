using System.Collections.Frozen;
using System.Globalization;

namespace Touchline.Domain.Matches;

/// <summary>
/// The envelope every match event carries on Event Hubs, as message properties, and the
/// tolerant-reader rules consumers apply to it before reading the body (ADR-023).
/// </summary>
/// <remarks>
/// The envelope is plain strings and numbers, so this type works on a property dictionary
/// rather than any Azure SDK type, and the domain stays free of cloud dependencies.
/// </remarks>
public static class EventEnvelope
{
    /// <summary>
    /// The schema version producers write: major.minor. A new event type, or a new optional
    /// field, bumps the minor, because older readers skip the type or ignore the field. A new
    /// value in an existing enum field (a new restart, body part or outcome) bumps the
    /// <b>major</b>: older readers can't parse the body at all, so they must recognise the
    /// event from its envelope and skip it knowingly (ADR-023).
    /// </summary>
    public const string CurrentSchemaVersion = "1.0";

    /// <summary>The property holding the schema version.</summary>
    public const string SchemaVersionProperty = "schemaVersion";

    /// <summary>The property holding the event type's name.</summary>
    public const string TypeProperty = "type";

    /// <summary>The property holding the match ID, which is also the partition key.</summary>
    public const string MatchIdProperty = "matchId";

    /// <summary>The property holding the event's sequence within its match.</summary>
    public const string SequenceProperty = "sequence";

    private const int _supportedMajorVersion = 1;

    // Checked for every message, so built once. Enum.TryParse would also accept numbers.
    private static readonly FrozenSet<string> _knownTypes = Enum.GetNames<MatchEventType>().ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The envelope properties for an event.
    /// </summary>
    /// <param name="matchEvent">The event being published.</param>
    /// <returns>The properties to set on the message.</returns>
    public static IReadOnlyDictionary<string, object> For(MatchEvent matchEvent)
    {
        ArgumentNullException.ThrowIfNull(matchEvent);

        return new Dictionary<string, object>
        {
            [SchemaVersionProperty] = CurrentSchemaVersion,
            [TypeProperty] = matchEvent.Type.ToString(),
            [MatchIdProperty] = matchEvent.MatchId,
            [SequenceProperty] = matchEvent.Sequence
        };
    }

    /// <summary>
    /// Decides whether a consumer should read a message's body, from its envelope alone.
    /// </summary>
    /// <param name="properties">The message's properties.</param>
    /// <returns>
    /// Accepted with the match ID and sequence, or skipped with a reason: a missing or
    /// malformed envelope, a newer major schema version, or an event type this build doesn't know.
    /// </returns>
    public static EnvelopeCheck Check(IReadOnlyDictionary<string, object> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        if (!TryGetString(properties, MatchIdProperty, out var matchId) || !TryGetSequence(properties, out var sequence))
        {
            return EnvelopeCheck.Skip(SkipReason.MissingEnvelope, null, null);
        }

        if (!TryGetString(properties, SchemaVersionProperty, out var version) || !TryGetMajor(version, out var major))
        {
            return EnvelopeCheck.Skip(SkipReason.MissingEnvelope, matchId, sequence);
        }

        if (major > _supportedMajorVersion)
        {
            return EnvelopeCheck.Skip(SkipReason.NewerSchema, matchId, sequence);
        }

        if (!TryGetString(properties, TypeProperty, out var type) || !_knownTypes.Contains(type))
        {
            return EnvelopeCheck.Skip(SkipReason.UnknownType, matchId, sequence);
        }

        return new EnvelopeCheck(null, matchId, sequence);
    }

    private static bool TryGetString(IReadOnlyDictionary<string, object> properties, string name, out string value)
    {
        value = properties.TryGetValue(name, out var raw) && raw is string text && !string.IsNullOrWhiteSpace(text) ? text : string.Empty;
        return value.Length > 0;
    }

    // Transports may hand the sequence back as any integer type, or as text.
    private static bool TryGetSequence(IReadOnlyDictionary<string, object> properties, out long sequence)
    {
        sequence = 0;
        return properties.TryGetValue(SequenceProperty, out var raw) && raw switch
        {
            long value => (sequence = value) > 0,
            int value => (sequence = value) > 0,
            string text => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out sequence) && sequence > 0,
            _ => false
        };
    }

    private static bool TryGetMajor(string version, out int major)
    {
        var majorText = version.Split('.')[0];
        return int.TryParse(majorText, NumberStyles.None, CultureInfo.InvariantCulture, out major) && major > 0;
    }
}

/// <summary>Why a tolerant reader skipped a message (ADR-023).</summary>
public enum SkipReason
{
    /// <summary>The envelope is missing or malformed.</summary>
    MissingEnvelope,

    /// <summary>The message uses a newer major schema version than this consumer understands.</summary>
    NewerSchema,

    /// <summary>The event type is one this consumer doesn't know.</summary>
    UnknownType,

    /// <summary>The body couldn't be read, or the event contradicts itself (see <see cref="MatchEvent.EnsureValid"/>).</summary>
    InvalidEvent
}

/// <summary>The result of checking a message's envelope.</summary>
/// <param name="Skipped">Why the message is skipped, or null when it should be read.</param>
/// <param name="MatchId">The match ID from the envelope, when there was one.</param>
/// <param name="Sequence">The sequence from the envelope, when there was one.</param>
public sealed record EnvelopeCheck(SkipReason? Skipped, string? MatchId, long? Sequence)
{
    /// <summary>True when the consumer should read the message's body.</summary>
    public bool Accepted => Skipped is null;

    internal static EnvelopeCheck Skip(SkipReason reason, string? matchId, long? sequence) => new(reason, matchId, sequence);
}
