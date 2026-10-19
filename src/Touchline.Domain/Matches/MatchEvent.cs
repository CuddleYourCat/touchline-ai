using System.Text.Json.Serialization;
using Touchline.Domain.Pitch;

namespace Touchline.Domain.Matches;

/// <summary>What happened in a match event.</summary>
public enum MatchEventType
{
    /// <summary>
    /// The match's context, always the first event (ADR-019). See <see cref="MatchEvent.Metadata"/>.
    /// By convention its team is <see cref="TeamSide.Home"/> and it starts on the centre spot.
    /// </summary>
    MatchMetadata,

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

    /// <summary>
    /// True when an opponent was closing the player down (ADR-019). Only valid on passes,
    /// carries and shots.
    /// </summary>
    public bool UnderPressure { get; init; }

    /// <summary>Shot detail, present only when <see cref="Type"/> is <see cref="MatchEventType.Shot"/>.</summary>
    public ShotDetail? Shot { get; init; }

    /// <summary>Pass detail, including xPass (ADR-019). Present only when <see cref="Type"/> is <see cref="MatchEventType.Pass"/>.</summary>
    public PassDetail? Pass { get; init; }

    /// <summary>Carry detail, the player's speed (ADR-019). Present only when <see cref="Type"/> is <see cref="MatchEventType.Carry"/>.</summary>
    public CarryDetail? Carry { get; init; }

    /// <summary>The match's context (ADR-019). Present only when <see cref="Type"/> is <see cref="MatchEventType.MatchMetadata"/>.</summary>
    public MatchMetadata? Metadata { get; init; }

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
    /// Checks that the event's parts agree with its type: each detail (shot, card colour,
    /// pass, carry, metadata) is present on its own type and on no other, a shot's
    /// <see cref="Successful"/> flag matches whether it was on target, each second-player
    /// field (ADR-017) appears only on its own event type, only passes, carries and shots
    /// are marked as under pressure, the metadata event names no player and no end, and the
    /// type, team and card colour are defined values (JSON accepts any number for an enum).
    /// </summary>
    /// <exception cref="ArgumentException">The event contradicts itself.</exception>
    public void EnsureValid()
    {
        var problem = UndefinedEnum()
            ?? DetailMismatch(MatchEventType.Shot, Shot, "shot detail")
            ?? DetailMismatch(MatchEventType.Card, Card, "card colour")
            ?? DetailMismatch(MatchEventType.Pass, Pass, "pass detail")
            ?? DetailMismatch(MatchEventType.Carry, Carry, "carry detail")
            ?? DetailMismatch(MatchEventType.MatchMetadata, Metadata, "match metadata")
            ?? this switch
            {
                { Shot: { } shot } when Successful != shot.OnTarget =>
                    $"a shot marked Successful = {Successful} has outcome {shot.Outcome}",
                { Type: not MatchEventType.Pass, RecipientPlayerId: not null } => $"a {Type} event has a recipient",
                { Type: not MatchEventType.Shot, AssistPlayerId: not null } => $"a {Type} event has an assist",
                { Type: not MatchEventType.Foul, FouledPlayerId: not null } => $"a {Type} event has a fouled player",
                { UnderPressure: true, Type: not (MatchEventType.Pass or MatchEventType.Carry or MatchEventType.Shot) } =>
                    $"a {Type} event is marked as under pressure",
                { Type: MatchEventType.MatchMetadata, PlayerId: not null } => "the metadata event names a player",
                { Type: MatchEventType.MatchMetadata, End: not null } => "the metadata event has an end position",
                _ => null
            };

        if (problem is not null)
        {
            throw new ArgumentException($"Event {Sequence} in match '{MatchId}' is invalid: {problem}.");
        }
    }

    private string? UndefinedEnum() =>
        !Enum.IsDefined(Type) ? $"the event type {Type} isn't defined"
        : !Enum.IsDefined(Team) ? $"the team {Team} isn't defined"
        : Card is { } card && !Enum.IsDefined(card) ? $"the card colour {card} isn't defined"
        : null;

    // A detail belongs to exactly one event type: required there, and forbidden everywhere else.
    private string? DetailMismatch(MatchEventType owner, object? detail, string name) => (Type == owner, detail is null) switch
    {
        (true, true) => $"a {owner} event has no {name}",
        (false, false) => $"a {Type} event has {name}",
        _ => null
    };
}
