using System.Globalization;
using Touchline.Domain.Matches;
using Touchline.Domain.Viewing;

namespace Touchline.Domain.Squad;

// The ports between lanes (ADR-027, ADR-030). The Match Centre and the agent host depend on
// these interfaces; the agent library and the search library implement them. Each lane fakes
// the others' ports in its tests, so no lane waits for another to finish.

/// <summary>
/// How much attention a moment gets. Key moments get the agents; notable moments get a
/// template line and no model call (backlog: the notable-moment tier).
/// </summary>
public enum MomentTier
{
    /// <summary>A key moment: goals, big chances, penalties, red cards.</summary>
    Key,

    /// <summary>A notable moment: yellow cards, corners, woodwork. Template commentary only.</summary>
    Notable,

    /// <summary>
    /// A minor moment: a throw-in or a goal kick (ADR-055). Only an Analyst is sent it, with a low-priority
    /// card and a template line.
    /// </summary>
    Minor
}

/// <summary>Where a line of commentary came from.</summary>
public enum CommentarySource
{
    /// <summary>A fixed template, filled in with the facts. No model call.</summary>
    Template,

    /// <summary>A model, through the agent squad.</summary>
    Model
}

/// <summary>What a citation in an answer points at.</summary>
public enum CitationKind
{
    /// <summary>A possession sequence in the search index (ADR-008).</summary>
    Possession,

    /// <summary>A key or notable moment.</summary>
    Moment,

    /// <summary>One match event, with the ID <c>event:{matchId}:{sequence}</c>.</summary>
    Event,

    /// <summary>The match summary, with the ID <c>match:{matchId}</c>.</summary>
    Match
}

/// <summary>
/// Builds the IDs shared by moments and the commentary written about them.
/// </summary>
public static class MomentIds
{
    /// <summary>
    /// A moment's ID: its match and the sequence of the event that triggered it.
    /// </summary>
    /// <param name="matchId">The match.</param>
    /// <param name="sequence">The triggering event's sequence number.</param>
    /// <returns>For example <c>demo-3:412</c>.</returns>
    public static string For(string matchId, long sequence) =>
        string.Create(CultureInfo.InvariantCulture, $"{matchId}:{sequence}");
}

/// <summary>
/// One line of commentary about a moment.
/// </summary>
/// <param name="MatchId">The match.</param>
/// <param name="MomentId">The moment it describes (<see cref="MomentIds.For"/>).</param>
/// <param name="Sequence">The triggering event's sequence number.</param>
/// <param name="Tier">The moment's tier.</param>
/// <param name="Source">A template or a model.</param>
/// <param name="Text">The line itself.</param>
/// <param name="Mode">The viewing mode it was written for.</param>
/// <param name="Language">The language it's written in.</param>
public sealed record CommentaryLine(
    string MatchId,
    string MomentId,
    long Sequence,
    MomentTier Tier,
    CommentarySource Source,
    string Text,
    ViewingMode Mode,
    string Language);

/// <summary>
/// Something an answer is based on, so a fan can check it.
/// </summary>
/// <param name="Kind">A possession, a moment, an event or the match summary.</param>
/// <param name="Id">Its ID.</param>
/// <param name="Label">A short label to show, such as <c>67:12 goal</c>.</param>
public sealed record Citation(CitationKind Kind, string Id, string Label);

/// <summary>
/// The Analyst's answer to a fan's question.
/// </summary>
/// <param name="Text">The answer.</param>
/// <param name="Citations">What it's based on. An answer with no citations should say it couldn't find support.</param>
/// <param name="Language">The BCP 47 tag of the language the answer is written in, such as <c>en</c> or <c>es-MX</c>.</param>
public sealed record FanAnswer(string Text, IReadOnlyList<Citation> Citations, string Language);

/// <summary>
/// The Producer's full-time recap.
/// </summary>
/// <param name="MatchId">The match.</param>
/// <param name="Headline">One line.</param>
/// <param name="Text">The recap.</param>
/// <param name="Mode">The viewing mode it was written for.</param>
/// <param name="Language">The language it's written in.</param>
/// <param name="Source">Who wrote it: <c>Template</c> (built from the facts, no model call) or <c>Model</c> (the Producer).</param>
public sealed record MatchRecap(string MatchId, string Headline, string Text, ViewingMode Mode, string Language, CommentarySource Source);

/// <summary>
/// A possession sequence found by search (ADR-008).
/// </summary>
/// <param name="Id">The document ID, <c>{matchId}:{possessionId}</c>.</param>
/// <param name="MatchId">The match.</param>
/// <param name="PossessionId">The possession within the match.</param>
/// <param name="Team">The team in possession.</param>
/// <param name="From">When the possession started.</param>
/// <param name="To">When it ended.</param>
/// <param name="Summary">The indexed text summary of the move.</param>
/// <param name="Score">The search relevance score, higher is better.</param>
public sealed record PossessionHit(
    string Id,
    string MatchId,
    int PossessionId,
    TeamSide Team,
    MatchTime From,
    MatchTime To,
    string Summary,
    double Score);

/// <summary>
/// The agent squad's on-demand work (ADR-030): hosted in the Match Centre behind the Q&amp;A
/// and recap endpoints. Implemented by <c>Touchline.Agents</c>.
/// </summary>
public interface ISquad
{
    /// <summary>
    /// Answers a fan's question about a match. The question is untrusted input: it must never
    /// be concatenated into a system prompt.
    /// </summary>
    /// <param name="matchId">The match.</param>
    /// <param name="question">The fan's question, already length-checked.</param>
    /// <param name="viewer">Who's asking, and how they want it told.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The answer, with citations.</returns>
    Task<FanAnswer> AnswerAsync(string matchId, string question, Viewer viewer, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the full-time recap for a finished match.
    /// </summary>
    /// <param name="matchId">The match, which must be at full time.</param>
    /// <param name="viewer">Who's reading, and how they want it told.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The recap.</returns>
    Task<MatchRecap> RecapAsync(string matchId, Viewer viewer, CancellationToken cancellationToken);
}

/// <summary>
/// Where commentary is kept (ADR-029): the agents write it, and the Match Centre reads it
/// through the change feed. Implemented over the Cosmos DB <c>commentary</c> container.
/// </summary>
public interface ICommentaryStore
{
    /// <summary>
    /// Saves a line, in one slot per moment, mode and language (the language tag normalised, so
    /// <c>es-mx</c> and <c>es-MX</c> share a slot). A model line replaces whatever is in the slot,
    /// a template or an older model line. A template line only fills an empty slot: if a line is
    /// already there it's kept, so a template never replaces a model line.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the line is stored, or kept out by a line already there.</returns>
    /// <exception cref="ArgumentException">The line's moment ID isn't <see cref="MomentIds.For"/> its match and sequence.</exception>
    Task SaveAsync(CommentaryLine line, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a match's commentary, in sequence order.
    /// </summary>
    /// <param name="matchId">The match.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Every stored line for the match.</returns>
    Task<IReadOnlyList<CommentaryLine>> GetAsync(string matchId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the one line in a moment's slot for a mode and language: a point read, not a query.
    /// </summary>
    /// <param name="matchId">The match.</param>
    /// <param name="momentId">The moment (<see cref="MomentIds.For"/>).</param>
    /// <param name="mode">The viewing mode.</param>
    /// <param name="language">The language tag, normalised as <see cref="SaveAsync"/> does.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The line, or null if the slot is empty.</returns>
    Task<CommentaryLine?> FindAsync(string matchId, string momentId, ViewingMode mode, string language, CancellationToken cancellationToken);
}

/// <summary>
/// Searches a match's possession sequences (ADR-008, ADR-026). The Analyst's retrieval tool.
/// Implemented by <c>Touchline.Search</c> over Azure AI Search.
/// </summary>
public interface IPossessionSearch
{
    /// <summary>
    /// Finds the possessions most relevant to a query, with hybrid search and semantic ranking.
    /// </summary>
    /// <param name="matchId">The match to search within.</param>
    /// <param name="query">What to look for, in plain words.</param>
    /// <param name="top">The most results to return.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The best matches, best first.</returns>
    Task<IReadOnlyList<PossessionHit>> SearchAsync(string matchId, string query, int top, CancellationToken cancellationToken);
}
