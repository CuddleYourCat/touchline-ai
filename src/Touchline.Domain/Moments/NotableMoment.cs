using Touchline.Domain.Matches;

namespace Touchline.Domain.Moments;

/// <summary>
/// The kinds of moment worth a card and a template line, but not a model call (ADR-046).
/// </summary>
public enum NotableMomentKind
{
    /// <summary>A player cautioned.</summary>
    YellowCard,

    /// <summary>The ball went over the goal line off a defender, so the attackers take a corner.</summary>
    Corner,

    /// <summary>A shot that hit the post or the bar and wasn't a key moment.</summary>
    Woodwork,

    /// <summary>Minor: the ball went over a touchline.</summary>
    ThrowIn,

    /// <summary>Minor: the ball went over the goal line off an attacker.</summary>
    GoalKick,

    /// <summary>A player taken off and a substitute brought on (ADR-064).</summary>
    Substitution
}

/// <summary>
/// A moment that gets an overlay card and a template line, and wakes no agent (ADR-046).
/// </summary>
/// <param name="Kind">What kind of moment it is.</param>
/// <param name="Trigger">The event that caused it.</param>
/// <param name="Reason">One plain sentence saying why it's notable, for logs and the moment's reason.</param>
/// <param name="Team">
/// The side the moment is about: the cautioned or shooting side, the side that wins a corner, a
/// throw-in or a goal kick. For a restart that is the opposite side to the trigger's, which is
/// the last team to touch the ball.
/// </param>
/// <param name="PlayerName">
/// The trigger's player, as the roster spells the name. Null when the trigger names nobody or
/// no squad has them; the template lines then say "a player" and never print a raw ID.
/// </param>
/// <param name="PlayerTeam">The short name of that player's club, or null when they aren't in a squad.</param>
/// <param name="ReplacementName">
/// For a substitution, the player coming on, as the roster spells the name; null otherwise, or
/// when no squad has him.
/// </param>
/// <param name="Ordinal">
/// How many moments of this kind came before it in the match, counting both teams and the whole
/// event stream (0 for the first corner). It only picks a template variant (ADR-070); set it with
/// <see cref="NotableOrdinals"/>. Not on the wire and not stored.
/// </param>
public sealed record NotableMoment(
    NotableMomentKind Kind,
    MatchEvent Trigger,
    string Reason,
    TeamSide Team,
    string? PlayerName,
    string? PlayerTeam,
    string? ReplacementName = null,
    int Ordinal = 0)
{
    /// <summary>
    /// True for a throw-in or a goal kick: it produces a moment, a low-priority card and a template
    /// line for the Analyst only, and nothing at all for the Casual viewer (ADR-055). The
    /// projector builds it on a side channel that the hub injects per subscriber.
    /// </summary>
    public bool IsMinor => Kind is NotableMomentKind.ThrowIn or NotableMomentKind.GoalKick;

    /// <summary>
    /// The player the moment features, or null when it's about a restart, where the trigger's
    /// player is only the last to touch the ball and belongs to the other side. A substitution
    /// features the player coming on.
    /// </summary>
    public string? FeaturedPlayerId => Kind switch
    {
        NotableMomentKind.YellowCard or NotableMomentKind.Woodwork => Trigger.PlayerId,
        NotableMomentKind.Substitution => Trigger.ReplacementPlayerId,
        _ => null
    };
}

/// <summary>
/// Numbers a match's notable moments within their kind, in event order, so the first, second and
/// third corner pick different template variants (ADR-070). One instance per walk of a match: feed
/// it every notable moment found, in sequence order, before any viewer filtering, so every viewer
/// and every reader sees the same numbering.
/// </summary>
public sealed class NotableOrdinals
{
    private readonly Dictionary<NotableMomentKind, int> _seen = [];

    /// <summary>The moment, numbered.</summary>
    /// <param name="moment">The next notable moment of the match.</param>
    /// <returns>The same moment with its ordinal within its kind.</returns>
    public NotableMoment Number(NotableMoment moment)
    {
        ArgumentNullException.ThrowIfNull(moment);
        var ordinal = _seen.GetValueOrDefault(moment.Kind);
        _seen[moment.Kind] = ordinal + 1;
        return moment with { Ordinal = ordinal };
    }
}
