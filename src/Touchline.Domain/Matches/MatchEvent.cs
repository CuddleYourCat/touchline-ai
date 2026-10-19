using System.Text.Json.Serialization;
using Touchline.Domain.Pitch;

namespace Touchline.Domain.Matches;

/// <summary>What happened in a match event.</summary>
public enum MatchEventType
{
    /// <summary>Play starts or restarts from the centre spot.</summary>
    KickOff,

    /// <summary>The ball is played to a team-mate.</summary>
    Pass,

    /// <summary>A player moves with the ball.</summary>
    Carry,

    /// <summary>A defender closes down the player on the ball.</summary>
    Pressure,

    /// <summary>A defender challenges for the ball.</summary>
    Tackle,

    /// <summary>A defender cuts out a pass.</summary>
    Interception,

    /// <summary>An attempt on goal. See <see cref="MatchEvent.Shot"/> for the detail.</summary>
    Shot,

    /// <summary>An infringement given by the referee.</summary>
    Foul,

    /// <summary>A yellow or red card. See <see cref="MatchEvent.Card"/>.</summary>
    Card,

    /// <summary>The end of a half.</summary>
    PeriodEnd
}

/// <summary>
/// One thing that happened in a match: the unit the simulator produces, Event Hubs
/// carries and the agents reason over (ADR-014). Events are immutable.
/// </summary>
public sealed record MatchEvent
{
    /// <summary>Position of this event in the match, starting at 1. Unique within a match.</summary>
    public required long Sequence { get; init; }

    /// <summary>The match this event belongs to.</summary>
    public required string MatchId { get; init; }

    /// <summary>Which possession this event is part of, so whole moves can be grouped (ADR-008).</summary>
    public required int PossessionId { get; init; }

    /// <summary>When it happened.</summary>
    public required MatchTime Time { get; init; }

    /// <summary>What happened.</summary>
    public required MatchEventType Type { get; init; }

    /// <summary>The team that performed the action.</summary>
    public required TeamSide Team { get; init; }

    /// <summary>The player who performed the action, or null for team events such as <see cref="MatchEventType.PeriodEnd"/>.</summary>
    public string? PlayerId { get; init; }

    /// <summary>Where the action started.</summary>
    public required PitchPosition Start { get; init; }

    /// <summary>Where the ball ended up, for passes, carries and shots.</summary>
    public PitchPosition? End { get; init; }

    /// <summary>
    /// Whether the action achieved its aim: a completed pass, a won tackle and so on. For a
    /// shot it means on target, and <see cref="EnsureValid"/> rejects a shot whose flag
    /// disagrees with its <see cref="ShotDetail.Outcome"/>.
    /// </summary>
    public bool Successful { get; init; } = true;

    /// <summary>Shot detail, present only when <see cref="Type"/> is <see cref="MatchEventType.Shot"/>.</summary>
    public ShotDetail? Shot { get; init; }

    /// <summary>The card shown, present only when <see cref="Type"/> is <see cref="MatchEventType.Card"/>.</summary>
    public CardColour? Card { get; init; }

    /// <summary>For a pass: the team-mate it was aimed at (ADR-017). Only valid on passes.</summary>
    public string? RecipientPlayerId { get; init; }

    /// <summary>For a shot: the team-mate whose pass or delivery created it (ADR-017). Only valid on shots.</summary>
    public string? AssistPlayerId { get; init; }

    /// <summary>For a foul: the opponent who was fouled (ADR-017). Only valid on fouls.</summary>
    public string? FouledPlayerId { get; init; }

    /// <summary>True when this event is a shot that went in.</summary>
    [JsonIgnore]
    public bool IsGoal => Shot is { Outcome: ShotOutcome.Goal };

    /// <summary>
    /// Checks that the event's parts agree with its type: a shot carries its detail, a card
    /// carries its colour, neither appears on any other type, a shot's
    /// <see cref="Successful"/> flag matches whether it was on target, and each second-player
    /// field (ADR-017) appears only on its own event type.
    /// </summary>
    /// <exception cref="ArgumentException">The event contradicts itself.</exception>
    public void EnsureValid()
    {
        var problem = this switch
        {
            { Type: MatchEventType.Shot, Shot: null } => "a shot has no shot detail",
            { Type: not MatchEventType.Shot, Shot: not null } => $"a {Type} event has shot detail",
            { Type: MatchEventType.Card, Card: null } => "a card event has no card colour",
            { Type: not MatchEventType.Card, Card: not null } => $"a {Type} event has a card colour",
            { Shot: { } shot } when Successful != shot.OnTarget =>
                $"a shot marked Successful = {Successful} has outcome {shot.Outcome}",
            { Type: not MatchEventType.Pass, RecipientPlayerId: not null } => $"a {Type} event has a recipient",
            { Type: not MatchEventType.Shot, AssistPlayerId: not null } => $"a {Type} event has an assist",
            { Type: not MatchEventType.Foul, FouledPlayerId: not null } => $"a {Type} event has a fouled player",
            _ => null
        };

        if (problem is not null)
        {
            throw new ArgumentException($"Event {Sequence} in match '{MatchId}' is invalid: {problem}.");
        }
    }
}
