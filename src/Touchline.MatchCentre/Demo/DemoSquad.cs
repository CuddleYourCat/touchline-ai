using System.Globalization;
using Touchline.Domain.Squad;
using Touchline.Domain.Viewing;

namespace Touchline.MatchCentre.Demo;

/// <summary>
/// A stand-in for the agent squad until <c>Touchline.Agents</c> exists (ADR-030). It makes no
/// model call. It says plainly that it's a stand-in, and answers only from the match's own
/// facts, so nothing it says can be mistaken for analysis.
/// </summary>
/// <param name="matches">The demo match source.</param>
internal sealed class DemoSquad(DemoMatchSource matches) : ISquad
{
    /// <summary>Starts every stand-in answer, so the UI and its readers can tell.</summary>
    public const string StandInNotice = "The Analyst isn't connected in this demo build.";

    /// <inheritdoc />
    public Task<FanAnswer> AnswerAsync(string matchId, string question, Viewer viewer, CancellationToken cancellationToken)
    {
        var (state, moments) = matches.Snapshot(matchId)
            ?? throw new KeyNotFoundException($"There's no match '{matchId}'.");
        var (home, away) = matches.TeamNames;

        var text = string.Create(CultureInfo.InvariantCulture,
            $"{StandInNotice} The score is {home} {state.Home.Goals}–{state.Away.Goals} {away} at {state.Time}, with {moments.Count} key moments so far.");
        IReadOnlyList<Citation> citations =
        [
            .. moments.TakeLast(1).Select(m => new Citation(
                CitationKind.Moment,
                MomentIds.For(matchId, m.Trigger.Sequence),
                string.Create(CultureInfo.InvariantCulture, $"{m.Trigger.Time} {m.Kind}")))
        ];
        return Task.FromResult(new FanAnswer(text, citations, "en"));
    }

    /// <inheritdoc />
    public Task<MatchRecap> RecapAsync(string matchId, Viewer viewer, CancellationToken cancellationToken)
    {
        var (state, moments) = matches.Snapshot(matchId)
            ?? throw new KeyNotFoundException($"There's no match '{matchId}'.");
        var (home, away) = matches.TeamNames;

        var headline = string.Create(CultureInfo.InvariantCulture, $"{home} {state.Home.Goals}–{state.Away.Goals} {away}");
        var opening = string.Create(
            CultureInfo.InvariantCulture,
            $"Full time: {home} {state.Home.Goals}, {away} {state.Away.Goals}.");
        var text = moments.Count == 0
            ? opening
            : string.Join(" ", moments.Select(m => m.Reason).Prepend($"{opening} The key moments:"));
        return Task.FromResult(new MatchRecap(matchId, headline, text, viewer.Mode, "en", CommentarySource.Template));
    }
}
