using Touchline.Domain.Matches;

namespace Touchline.Domain.Moments;

/// <summary>The kinds of milestone a match can reach (backlog: milestones).</summary>
public enum MilestoneKind
{
    /// <summary>The first goal of the match, by either side.</summary>
    FirstGoal,

    /// <summary>A player's second goal of the match.</summary>
    Brace,

    /// <summary>A player's third goal of the match.</summary>
    HatTrick,

    /// <summary>A player's 50th completed pass of the match.</summary>
    FiftiethPass,

    /// <summary>A side that hasn't conceded by the end of the first half.</summary>
    CleanSheetAtHalfTime,

    /// <summary>The first card of the match, of either colour, to either side.</summary>
    FirstBooking,

    /// <summary>A carry faster than the player's profile top speed and any earlier carry of theirs this match (ADR-019).</summary>
    PersonalBestSpeed
}

/// <summary>
/// A milestone the match has just reached. Milestones feed the Producer's recap and the
/// notable-moment tier, so each carries a plain sentence like a key moment's reason.
/// </summary>
/// <param name="Kind">Which milestone.</param>
/// <param name="Trigger">The event that reached it.</param>
/// <param name="Side">The side the milestone belongs to: the scorer's, passer's, carrier's or carded player's side, or the side keeping the clean sheet.</param>
/// <param name="PlayerId">The player who reached it, or null for a clean sheet, which belongs to the whole side.</param>
/// <param name="Reason">One plain sentence, starting with the trigger's match time, for logs, agent context and the recap.</param>
public sealed record Milestone(MilestoneKind Kind, MatchEvent Trigger, TeamSide Side, string? PlayerId, string Reason);
