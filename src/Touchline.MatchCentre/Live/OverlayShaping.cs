using System.Globalization;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;

namespace Touchline.MatchCentre.Live;

/// <summary>
/// The numbers an Analyst subtitle can be built from, taken when the event was projected.
/// </summary>
/// <param name="ShotXg">The xG of the shot on the event the card is about, or null when it isn't a shot.</param>
/// <param name="HomeXg">The home side's match xG after the event, or null when it isn't known.</param>
/// <param name="AwayXg">The away side's match xG after the event, or null when it isn't known.</param>
internal sealed record OverlayContext(double? ShotXg, double? HomeXg, double? AwayXg)
{
    /// <summary>No numbers at all, so an Analyst subtitle is never invented.</summary>
    public static OverlayContext None { get; } = new(null, null, null);
}

/// <summary>
/// A minor moment (a throw-in or a goal kick) carried beside the shared messages (ADR-055), so
/// only an Analyst subscriber is ever sent it.
/// </summary>
/// <param name="Moment">The moment's message.</param>
/// <param name="Card">Its neutral low-priority card, or null when a key card just went out.</param>
internal sealed record MinorExtras(MomentView Moment, OverlayCard? Card);

/// <summary>
/// Shapes the neutral overlay cards for one viewer (ADR-055). Pure, and used by both the live
/// hub and the demo source, so the two can't drift.
/// </summary>
internal static class OverlayShaping
{
    private const string _xgMetric = "xG";

    // The Casual viewer's titles, keyed by card type (the lower-cased moment kind). A type that
    // isn't here keeps the neutral broadcast-style title.
    private static readonly Dictionary<string, string> _friendlyTitles = new(StringComparer.Ordinal)
    {
        ["goal"] = "Goal!",
        ["bigchance"] = "Big chance",
        ["penaltyawarded"] = "Penalty",
        ["redcard"] = "Red card",
        ["penaltysaved"] = "Penalty saved",
        ["penaltymissed"] = "Penalty missed",
        ["clearedoffline"] = "Cleared off the line",
        ["yellowcard"] = "Yellow card",
        ["corner"] = "Corner",
        ["woodwork"] = "Off the woodwork",
        ["substitution"] = "Substitution"
    };

    /// <summary>
    /// Shapes one card for a viewer. The Casual viewer gets a plain title, no subtitle and no
    /// low-priority card. The Analyst gets an xG subtitle for a shot, or the match xG when xG is
    /// their favourite metric. The ID, time and duration are kept, so a client's de-duplication
    /// by ID still holds after a reconnect. The neutral card is never changed.
    /// </summary>
    /// <param name="neutral">The viewer-neutral card.</param>
    /// <param name="context">The numbers the subtitle may use.</param>
    /// <param name="viewer">Who is watching.</param>
    /// <returns>The card to send, or null when this viewer gets none.</returns>
    public static OverlayCard? Shape(OverlayCard neutral, OverlayContext context, Viewer viewer)
    {
        ArgumentNullException.ThrowIfNull(neutral);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(viewer);

        var analyst = viewer.Mode == ViewingMode.Analyst;
        if (!analyst && neutral.Priority == OverlayCardBuilder.LowPriority)
        {
            return null;
        }

        var subtitle = analyst ? SubtitleFor(context, viewer) : null;
        var title = !analyst && _friendlyTitles.TryGetValue(neutral.Type, out var friendly) ? friendly : neutral.Payload.Title;
        return neutral.Payload.Subtitle == subtitle && neutral.Payload.Title == title
            ? neutral
            : neutral with { Payload = neutral.Payload with { Title = title, Subtitle = subtitle } };
    }

    /// <summary>
    /// Builds the messages one viewer is sent for an event: the shared messages with the overlay
    /// shaped for them, and for the Analyst the minor moment and its card ahead of the state.
    /// </summary>
    /// <param name="shared">The viewer-neutral messages for the event.</param>
    /// <param name="context">The numbers the subtitle may use.</param>
    /// <param name="minor">The event's minor moment, or null.</param>
    /// <param name="viewer">Who is watching.</param>
    /// <returns>The messages, in order. The shared list itself when nothing changes for this viewer.</returns>
    public static IReadOnlyList<LiveMessage> Apply(IReadOnlyList<LiveMessage> shared, OverlayContext context, MinorExtras? minor, Viewer viewer)
    {
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(viewer);

        // The Casual viewer never sees the side channel.
        minor = viewer.Mode == ViewingMode.Analyst ? minor : null;
        if (minor is null && !shared.Any(m => m.Event == StreamEvents.Overlay))
        {
            return shared;
        }

        var messages = new List<LiveMessage>(shared.Count + 2);
        foreach (var message in shared)
        {
            switch (message)
            {
                case { Event: StreamEvents.Overlay, Data: OverlayCard card }:
                    if (Shape(card, context, viewer) is { } shaped)
                    {
                        messages.Add(message with { Data = shaped });
                    }

                    break;
                case { Event: StreamEvents.State } when minor is not null:
                    messages.Add(new LiveMessage(StreamEvents.Moment, message.Id, minor.Moment));
                    if (minor.Card is { } minorCard && Shape(minorCard, context, viewer) is { } shapedMinor)
                    {
                        messages.Add(new LiveMessage(StreamEvents.Overlay, message.Id, shapedMinor));
                    }

                    messages.Add(message);
                    break;
                default:
                    messages.Add(message);
                    break;
            }
        }

        return messages;
    }

    // A shot shows its own xG. Anything else shows the match xG only for a viewer whose favourite
    // metric is xG and only when both totals are known; otherwise there's no subtitle.
    private static string? SubtitleFor(OverlayContext context, Viewer viewer) => context switch
    {
        { ShotXg: { } shot } => string.Create(CultureInfo.InvariantCulture, $"xG {shot:0.00}"),
        { HomeXg: { } home, AwayXg: { } away } when string.Equals(viewer.FavouriteMetric, _xgMetric, StringComparison.OrdinalIgnoreCase)
            => string.Create(CultureInfo.InvariantCulture, $"xG {home:0.00} - {away:0.00}"),
        _ => null
    };
}
