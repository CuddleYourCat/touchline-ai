using System.Globalization;
using System.Text;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;

namespace Touchline.Search;

/// <summary>A possession document with its template summary, and the key moment it ended in if any.</summary>
/// <param name="Document">The document, with the template summary and no vector.</param>
/// <param name="KeyMoment">The key moment the possession ended in, which makes it a candidate for a model-written summary.</param>
/// <param name="TeamName">The club in possession, by display name.</param>
public sealed record SummarizedPossession(PossessionDocument Document, KeyMomentKind? KeyMoment, string TeamName);

/// <summary>
/// Cuts a match's events into possession documents (ADR-008, ADR-056). Pure: the same events give
/// the same documents, so a redelivered message rewrites the same ones.
/// </summary>
/// <remarks>
/// There is no re-segmentation: events are grouped by the simulator's <see cref="MatchEvent.PossessionId"/>
/// in <see cref="MatchEvent.Sequence"/> order. A group with no pass, carry or shot is skipped. Players
/// come from the roster, never from raw IDs; an ID the roster doesn't know is left out.
/// </remarks>
public static class PossessionSummarizer
{
    /// <summary>The most characters in a summary, about 120 tokens (ADR-056).</summary>
    public const int MaxSummaryChars = 600;

    /// <summary>How many players open the passing chain of a long move.</summary>
    public const int ChainHead = 6;

    /// <summary>How many players close the passing chain of a long move.</summary>
    public const int ChainTail = 3;

    private static readonly IReadOnlyDictionary<KeyMomentKind, (string EndedIn, string Outcome)> _keyMoments =
        new Dictionary<KeyMomentKind, (string, string)>
        {
            [KeyMomentKind.Goal] = ("goal", "ended in a goal"),
            [KeyMomentKind.BigChance] = ("big_chance", "ended in a big chance"),
            [KeyMomentKind.PenaltyAwarded] = ("penalty_awarded", "won a penalty"),
            [KeyMomentKind.RedCard] = ("red_card", "ended with a red card"),
            [KeyMomentKind.PenaltySaved] = ("penalty_saved", "ended with a saved penalty"),
            [KeyMomentKind.PenaltyMissed] = ("penalty_missed", "ended with a missed penalty"),
            [KeyMomentKind.ClearedOffLine] = ("cleared_off_line", "ended with a shot cleared off the line")
        };

    /// <summary>Builds the possession documents for a match.</summary>
    /// <param name="matchId">The match; letters, digits, <c>-</c>, <c>_</c> or <c>=</c> only (it becomes part of each key).</param>
    /// <param name="events">The match's stored events.</param>
    /// <param name="detector">Finds the key moments again from the events.</param>
    /// <param name="roster">The names the text may use.</param>
    /// <returns>One summarised possession for each group with on-ball play, in match order.</returns>
    public static IReadOnlyList<SummarizedPossession> Summarize(
        string matchId,
        IReadOnlyList<MatchEvent> events,
        KeyMomentDetector detector,
        PossessionRoster roster)
    {
        ArgumentException.ThrowIfNullOrEmpty(matchId);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(roster);

        // A substitution (ADR-064) happens at a stoppage, not in a move, so it never ends or joins one.
        var ordered = events.Where(e => e.Type is not (MatchEventType.MatchMetadata or MatchEventType.Substitution)).OrderBy(e => e.Sequence).ToList();
        var moments = FindMoments(matchId, events.OrderBy(e => e.Sequence), detector);

        var result = new List<SummarizedPossession>();
        foreach (var group in ordered.GroupBy(e => e.PossessionId))
        {
            var members = group.ToList();
            var onBall = members.Where(IsOnBall).ToList();
            if (onBall.Count == 0)
            {
                continue;
            }

            result.Add(Build(matchId, group.Key, members, onBall, moments, roster));
        }

        return result;
    }

    /// <summary>Shortens text to <see cref="MaxSummaryChars"/>, at a word boundary, with an ellipsis.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text, unchanged when it fits.</returns>
    public static string Truncate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= MaxSummaryChars)
        {
            return text;
        }

        const string ellipsis = "...";
        var cut = text.LastIndexOf(' ', MaxSummaryChars - ellipsis.Length);
        var end = cut > 0 ? cut : MaxSummaryChars - ellipsis.Length;
        return text[..end].TrimEnd() + ellipsis;
    }

    private static bool IsOnBall(MatchEvent e) => e.Type is MatchEventType.Pass or MatchEventType.Carry or MatchEventType.Shot;

    private static Dictionary<long, KeyMomentKind> FindMoments(string matchId, IEnumerable<MatchEvent> events, KeyMomentDetector detector)
    {
        var moments = new Dictionary<long, KeyMomentKind>();
        var state = MatchState.KickOff(matchId);
        foreach (var e in events)
        {
            if (detector.Detect(e, state) is { } moment)
            {
                moments[e.Sequence] = moment.Kind;
            }

            state = state.Apply(e);
        }

        return moments;
    }

    private static SummarizedPossession Build(
        string matchId,
        int possessionId,
        List<MatchEvent> members,
        List<MatchEvent> onBall,
        Dictionary<long, KeyMomentKind> moments,
        PossessionRoster roster)
    {
        var first = members[0];
        var last = members[^1];
        var side = onBall[0].Team;
        var teamName = roster.NameOf(side);

        var moment = moments.TryGetValue(last.Sequence, out var atEnd)
            ? atEnd
            : members.Where(m => moments.ContainsKey(m.Sequence)).Select(m => (KeyMomentKind?)moments[m.Sequence]).LastOrDefault();
        var (endedIn, outcome) = Ending(last, moment);

        var chain = Collapse(onBall.Select(e => roster.NameOf(e.PlayerId)).OfType<string>());
        var players = members
            .SelectMany(e => new[] { e.PlayerId, e.RecipientPlayerId, e.AssistPlayerId, e.FouledPlayerId, e.BlockedByPlayerId })
            .Select(roster.NameOf)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var summary = Truncate(string.Create(
            CultureInfo.InvariantCulture,
            $"{teamName} possession from {first.Time} to {last.Time}. {Chain(chain)} The move {outcome}."));

        var document = new PossessionDocument
        {
            Id = PossessionDocument.KeyFor(matchId, possessionId),
            MatchId = matchId,
            PossessionId = possessionId.ToString(CultureInfo.InvariantCulture),
            Team = side.ToString(),
            FromClock = first.Time.ToString(),
            ToClock = last.Time.ToString(),
            FromSeconds = PossessionDocument.SecondsFor(first.Time),
            Players = players,
            EndedIn = endedIn,
            Summary = summary
        };
        return new SummarizedPossession(document, moment, teamName);
    }

    private static (string EndedIn, string Outcome) Ending(MatchEvent last, KeyMomentKind? moment)
    {
        if (moment is { } kind)
        {
            return _keyMoments[kind];
        }

        return last switch
        {
            { Shot.Outcome: ShotOutcome.Saved } => ("saved", "ended with a saved shot"),
            { Shot.Outcome: ShotOutcome.Blocked } => ("blocked", "ended with a blocked shot"),
            { Type: MatchEventType.Shot } => ("off_target", "ended with a shot off target"),
            { Type: MatchEventType.Foul } => ("foul", "ended with a foul"),
            { Type: MatchEventType.Card } => ("card", "ended with a card"),
            { Type: MatchEventType.BallOutOfPlay } => ("out", "ended when the ball went out of play"),
            { Type: MatchEventType.PeriodEnd } => ("period_end", "ended at the end of the half"),
            _ => ("turnover", "ended when the ball was lost")
        };
    }

    // Consecutive touches by one player read as one.
    private static List<string> Collapse(IEnumerable<string> names)
    {
        var chain = new List<string>();
        foreach (var name in names)
        {
            if (chain.Count == 0 || chain[^1] != name)
            {
                chain.Add(name);
            }
        }

        return chain;
    }

    private static string Chain(List<string> chain)
    {
        if (chain.Count == 0)
        {
            return "No named player was involved.";
        }

        if (chain.Count == 1)
        {
            return $"{chain[0]} had the ball throughout.";
        }

        var text = new StringBuilder("The ball went through ");
        if (chain.Count <= ChainHead + ChainTail)
        {
            text.AppendJoin(", ", chain);
        }
        else
        {
            var skipped = chain.Count - ChainHead - ChainTail;
            text.AppendJoin(", ", chain.Take(ChainHead));
            text.Append(CultureInfo.InvariantCulture, $", then after {skipped} more touches ");
            text.AppendJoin(", ", chain.TakeLast(ChainTail));
        }

        return text.Append('.').ToString();
    }
}
