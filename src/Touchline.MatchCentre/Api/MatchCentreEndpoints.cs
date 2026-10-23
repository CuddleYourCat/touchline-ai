using System.Globalization;
using System.Net.ServerSentEvents;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Touchline.Domain.Analytics;
using Touchline.Domain.Season;
using Touchline.Domain.Squad;
using Touchline.MatchCentre.Demo;
using Touchline.MatchCentre.Live;

namespace Touchline.MatchCentre.Api;

/// <summary>
/// The Match Centre API (ADR-027). Every handler goes through a port (<see cref="IMatchReadModel"/>,
/// <see cref="ILiveFeed"/>, <see cref="ISquad"/>), so the demo source and the live read side are
/// interchangeable behind the same endpoints.
/// </summary>
internal static partial class MatchCentreEndpoints
{
    /// <summary>Events per page when the client doesn't say.</summary>
    public const int DefaultEventLimit = 200;

    /// <summary>The most events one page returns.</summary>
    public const int MaxEventLimit = 1000;

    private const string _streamDescription =
        "Server-sent events (text/event-stream). Each message has an event name, an id (the last event sequence " +
        "the client has fully seen, for Last-Event-ID resume) and one JSON data line. The event names and their " +
        "data schemas: state = LiveState, event = MatchEvent, moment = MomentView, commentary = CommentaryLine, " +
        "analysis = MomentAnalysis, milestone = MilestoneView, momentum = MomentumReading, overlay = OverlayCard, " +
        "shape = ShapeView (modelled player positions after an event, not tracking data), fulltime = LiveState (sent once), heartbeat = Heartbeat (no id). Without after or Last-Event-ID the " +
        "stream starts from now, with a state snapshot. The stream ends after fulltime.";

    /// <summary>Maps every API endpoint under <c>/api</c>.</summary>
    /// <param name="app">The app.</param>
    /// <returns>The app, for chaining.</returns>
    public static IEndpointRouteBuilder MapMatchCentreApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", () => TypedResults.Ok(new MatchCentreStatus("touchline-ai", "synthetic")))
            .WithName("GetStatus").WithTags("Status").WithSummary("What the service is, and where its data comes from.");

        var matches = api.MapGroup("/matches").WithTags("Matches");
        matches.MapGet("/", ListMatches).WithName("ListMatches").WithSummary("Matches, newest first.")
            .ProducesValidationProblem();
        matches.MapGet("/{matchId}", GetMatch).WithName("GetMatch").WithSummary("A match in full.")
            .ProducesProblem(StatusCodes.Status404NotFound);
        matches.MapGet("/{matchId}/events", GetEvents).WithName("GetEvents").WithSummary("A page of a match's events.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);
        matches.MapGet("/{matchId}/momentum", GetMomentum).WithName("GetMomentum").WithSummary("Momentum so far, in five-minute windows.")
            .ProducesProblem(StatusCodes.Status404NotFound);
        matches.MapGet("/{matchId}/moments", GetMoments).WithName("GetMoments").WithSummary("Key and notable moments so far, with commentary for the viewer.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);
        matches.MapGet("/{matchId}/stream", Stream).WithName("StreamMatch").WithSummary("The live match, as server-sent events.")
            .WithDescription(_streamDescription)
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);
        matches.MapPost("/{matchId}/questions", AskQuestion).WithName("AskQuestion").WithSummary("Ask the Analyst about a match.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status429TooManyRequests).RequireRateLimiting(RateLimiting.PaidCalls);
        matches.MapGet("/{matchId}/recap", GetRecap).WithName("GetRecap").WithSummary("The full-time recap.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status429TooManyRequests).RequireRateLimiting(RateLimiting.PaidCalls);

        api.MapTeamBuilder();

        api.MapGet("/seasons/{season}/players/{playerId}", GetPlayerSeason).WithTags("Seasons")
            .WithName("GetPlayerSeason").WithSummary("A player's season card.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Results<Ok<IReadOnlyList<MatchSummary>>, ValidationProblem>> ListMatches(
        [FromQuery] string? status,
        [FromQuery] string? season,
        IMatchReadModel matches,
        CancellationToken cancellationToken)
    {
        var wanted = EnumNames.Parse<MatchStatus>(status);
        if (status is not null && wanted is null)
        {
            return Invalid("status", "The status must be scheduled, live or fulltime.");
        }

        return TypedResults.Ok(await matches.ListMatchesAsync(wanted, season, cancellationToken));
    }

    private static async Task<Results<Ok<MatchDetail>, ProblemHttpResult>> GetMatch(
        string matchId,
        IMatchReadModel matches,
        CancellationToken cancellationToken) =>
        await matches.GetMatchAsync(matchId, cancellationToken) is { } match
            ? TypedResults.Ok(match)
            : NoMatch(matchId);

    private static async Task<Results<Ok<EventPage>, ValidationProblem, ProblemHttpResult>> GetEvents(
        string matchId,
        [FromQuery] long? after,
        [FromQuery] int? limit,
        IMatchReadModel matches,
        CancellationToken cancellationToken)
    {
        if (after is < 0)
        {
            return Invalid("after", "after is an event sequence, from 0.");
        }

        if (limit is < 1 or > MaxEventLimit)
        {
            return Invalid("limit", $"limit is from 1 to {MaxEventLimit}.");
        }

        return await matches.GetEventsAsync(matchId, after ?? 0, limit ?? DefaultEventLimit, cancellationToken) is { } page
            ? TypedResults.Ok(page)
            : NoMatch(matchId);
    }

    private static async Task<Results<Ok<IReadOnlyList<MomentumReading>>, ProblemHttpResult>> GetMomentum(
        string matchId,
        IMatchReadModel matches,
        CancellationToken cancellationToken) =>
        await matches.GetMomentumAsync(matchId, cancellationToken) is { } readings
            ? TypedResults.Ok(readings)
            : NoMatch(matchId);

    private static async Task<Results<Ok<IReadOnlyList<MomentView>>, ValidationProblem, ProblemHttpResult>> GetMoments(
        string matchId,
        [AsParameters] ViewerQuery query,
        ITeamStore teams,
        IMatchReadModel matches,
        CancellationToken cancellationToken)
    {
        var (viewer, problems) = await query.ResolveAsync(teams, cancellationToken);
        if (viewer is null)
        {
            return TypedResults.ValidationProblem(problems);
        }

        return await matches.GetMomentsAsync(matchId, viewer, cancellationToken) is { } moments
            ? TypedResults.Ok(moments)
            : NoMatch(matchId);
    }

    private static async Task<Results<ServerSentEventsResult<object>, ValidationProblem, ProblemHttpResult>> Stream(
        string matchId,
        [FromQuery] long? after,
        [FromHeader(Name = "Last-Event-ID")] string? lastEventId,
        [AsParameters] ViewerQuery query,
        ITeamStore teams,
        IMatchReadModel matches,
        ILiveFeed feed,
        IOptions<StreamOptions> options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        // A browser reconnecting sends Last-Event-ID itself, and it's more recent than the
        // after the page first asked for, so it wins.
        long? from = after;
        if (lastEventId is not null)
        {
            if (!long.TryParse(lastEventId, NumberStyles.None, CultureInfo.InvariantCulture, out var resumed))
            {
                return Invalid("Last-Event-ID", "Last-Event-ID is an event sequence.");
            }

            from = resumed;
        }

        if (from is < 0)
        {
            return Invalid("after", "after is an event sequence, from 0.");
        }

        var (viewer, problems) = await query.ResolveAsync(teams, cancellationToken);
        if (viewer is null)
        {
            return TypedResults.ValidationProblem(problems);
        }

        if (await matches.GetMatchAsync(matchId, cancellationToken) is null)
        {
            return NoMatch(matchId);
        }

        // Before the response starts: a source that's down becomes a 503, not a stream that hangs.
        await feed.PrepareAsync(matchId, cancellationToken);
        var messages = HeartbeatStream.WithHeartbeats(
            feed.FollowAsync(matchId, from, viewer, cancellationToken),
            options.Value.HeartbeatInterval,
            time,
            cancellationToken);
        return TypedResults.ServerSentEvents(ToSse(messages, cancellationToken));
    }

    private static async Task<Results<Ok<FanAnswer>, ValidationProblem, ProblemHttpResult>> AskQuestion(
        string matchId,
        QuestionRequest request,
        [AsParameters] ViewerQuery query,
        ITeamStore teams,
        IMatchReadModel matches,
        ISquad squad,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        // Untrusted input (CLAUDE.md): checked here for shape and size; the agents keep it out of
        // their system prompts.
        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > QuestionRequest.MaxLength)
        {
            return Invalid("question", $"A question is 1 to {QuestionRequest.MaxLength} characters.");
        }

        var (viewer, problems) = await query.ResolveAsync(teams, cancellationToken);
        if (viewer is null)
        {
            return TypedResults.ValidationProblem(problems);
        }

        if (await matches.GetMatchAsync(matchId, cancellationToken) is null)
        {
            return NoMatch(matchId);
        }

        try
        {
            return TypedResults.Ok(await squad.AnswerAsync(matchId, request.Question.Trim(), viewer, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSquadFailed(loggers.CreateLogger(typeof(MatchCentreEndpoints)), ex, "question", matchId);
            return Unavailable("The Analyst can't answer right now. The match feed carries on; try again shortly.");
        }
    }

    private static async Task<Results<Ok<MatchRecap>, ValidationProblem, ProblemHttpResult>> GetRecap(
        string matchId,
        [AsParameters] ViewerQuery query,
        ITeamStore teams,
        IMatchReadModel matches,
        ISquad squad,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var (viewer, problems) = await query.ResolveAsync(teams, cancellationToken);
        if (viewer is null)
        {
            return TypedResults.ValidationProblem(problems);
        }

        if (await matches.GetMatchAsync(matchId, cancellationToken) is not { } match)
        {
            return NoMatch(matchId);
        }

        if (!match.State.FullTime)
        {
            return TypedResults.Problem(
                title: "Not full time yet",
                detail: "The recap is written after the final whistle.",
                statusCode: StatusCodes.Status409Conflict);
        }

        try
        {
            return TypedResults.Ok(await squad.RecapAsync(matchId, viewer, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSquadFailed(loggers.CreateLogger(typeof(MatchCentreEndpoints)), ex, "recap", matchId);
            return Unavailable("The recap isn't available right now. Try again shortly.");
        }
    }

    private static async Task<Results<Ok<PlayerSeason>, ProblemHttpResult>> GetPlayerSeason(
        string season,
        string playerId,
        IMatchReadModel matches,
        CancellationToken cancellationToken) =>
        await matches.GetPlayerSeasonAsync(season, playerId, cancellationToken) is { } card
            ? TypedResults.Ok(card)
            : NotFound($"Player '{playerId}' has no appearances in season '{season}'.");

    private static async IAsyncEnumerable<SseItem<object>> ToSse(
        IAsyncEnumerable<LiveMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var message in messages.WithCancellation(cancellationToken))
        {
            yield return new SseItem<object>(message.Data, message.Event) { EventId = message.Id };
        }
    }

    internal static ProblemHttpResult NoMatch(string matchId) => NotFound($"There's no match '{matchId}'.");

    internal static ProblemHttpResult NotFound(string detail) =>
        TypedResults.Problem(title: "Not found", detail: detail, statusCode: StatusCodes.Status404NotFound);

    internal static ProblemHttpResult Unavailable(string detail) =>
        TypedResults.Problem(title: "Temporarily unavailable", detail: detail, statusCode: StatusCodes.Status503ServiceUnavailable);

    internal static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    [LoggerMessage(Level = LogLevel.Error, Message = "The squad failed a {Request} for match {MatchId}")]
    private static partial void LogSquadFailed(ILogger logger, Exception exception, string request, string matchId);
}

/// <summary>
/// What the Match Centre reports about itself.
/// </summary>
/// <param name="Service">The service name.</param>
/// <param name="DataSource">Where match data comes from. Always <c>synthetic</c>, because every club, player and event is generated.</param>
internal sealed record MatchCentreStatus(string Service, string DataSource);
