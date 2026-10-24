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
    GoalKick
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
public sealed record NotableMoment(
    NotableMomentKind Kind,
    MatchEvent Trigger,
    string Reason,
    TeamSide Team,
    string? PlayerName,
    string? PlayerTeam)
{
    /// <summary>
    /// True for a throw-in or a goal kick: it produces a moment, a low-priority card and a template
    /// line for the Analyst only, and nothing at all for the Casual viewer (ADR-055). The
    /// projector builds it on a side channel that the hub injects per subscriber.
    /// </summary>
    public bool IsMinor => Kind is NotableMomentKind.ThrowIn or NotableMomentKind.GoalKick;

    /// <summary>
    /// The player the moment features, or null when it's about a restart, where the trigger's
    /// player is only the last to touch the ball and belongs to the other side.
    /// </summary>
    public string? FeaturedPlayerId => Kind is NotableMomentKind.YellowCard or NotableMomentKind.Woodwork
        ? Trigger.PlayerId
        : null;
}
