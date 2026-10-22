using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;
using Touchline.MatchCentre.TeamBuilding;

namespace Touchline.MatchCentre.Api;

/// <summary>
/// Build your own team (ADR-031) and fan-started matches (ADR-032). Every handler goes through
/// a port (<see cref="ITeamStore"/>, <see cref="INameGuard"/>, <see cref="IMatchLauncher"/>), so
/// the demo implementations and the team-builder lane's are interchangeable.
/// </summary>
internal static class TeamBuilderEndpoints
{
    /// <summary>The request header that makes a match request safe to repeat.</summary>
    public const string IdempotencyHeader = "Idempotency-Key";

    private const int _maxIdempotencyKeyLength = 64;
    private const int _defaultSuggestions = 5;

    /// <summary>Maps the team and team-builder endpoints, and match creation.</summary>
    /// <param name="api">The <c>/api</c> group.</param>
    /// <returns>The group, for chaining.</returns>
    public static RouteGroupBuilder MapTeamBuilder(this RouteGroupBuilder api)
    {
        var builder = api.MapGroup("/team-builder").WithTags("Team builder");
        builder.MapGet("/options", () => TypedResults.Ok(TeamBuilderCatalog.Options))
            .WithName("GetTeamBuilderOptions").WithSummary("Archetypes, manager styles, crest parts and the squad rules.");
        builder.MapGet("/names", SuggestNames).WithName("SuggestNames").WithSummary("Suggested fictional names.")
            .ProducesValidationProblem();
        builder.MapPost("/names/check", CheckNames).WithName("CheckNames").WithSummary("Checks names as a fan types them.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status429TooManyRequests)
            .RequireRateLimiting(RateLimiting.PaidCalls);

        var teams = api.MapGroup("/teams").WithTags("Teams");
        teams.MapGet("/", ListTeams).WithName("ListTeams").WithSummary("The clubs, built in and fan-built.")
            .ProducesValidationProblem();
        teams.MapGet("/{teamId}", GetTeam).WithName("GetTeam").WithSummary("A club in full, with its ETag.")
            .ProducesProblem(StatusCodes.Status404NotFound);
        teams.MapPost("/", CreateTeam).WithName("CreateTeam").WithSummary("Creates a fan team, and returns its edit key once.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status429TooManyRequests)
            .RequireRateLimiting(RateLimiting.TeamCreation);
        teams.MapPut("/{teamId}", ReplaceTeam).WithName("ReplaceTeam").WithSummary("Replaces a fan team. Needs its edit key.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status412PreconditionFailed);
        teams.MapDelete("/{teamId}", DeleteTeam).WithName("DeleteTeam").WithSummary("Deletes a fan team. Needs its edit key.")
            .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        teams.MapGet("/{teamId}/crest.svg", GetCrest).WithName("GetCrest").WithSummary("A club's crest, as SVG.")
            .Produces<string>(StatusCodes.Status200OK, CrestSvg.ContentType)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/matches", StartMatch).WithTags("Matches").WithName("StartMatch")
            .WithSummary("Starts an exhibition between two teams, with optional team sheets.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return api;
    }

    private static Results<Ok<NameSuggestions>, ValidationProblem> SuggestNames(
        [FromQuery] string? kind,
        [FromQuery] int? count,
        NameSuggester suggester)
    {
        if (EnumNames.Parse<NameKind>(kind) is not { } nameKind || nameKind == NameKind.Abbreviation)
        {
            return MatchCentreEndpoints.Invalid("kind", "kind is team, venue, manager or player.");
        }

        if (count is < 1 or > NameSuggester.MaxCount)
        {
            return MatchCentreEndpoints.Invalid("count", $"count is 1 to {NameSuggester.MaxCount}.");
        }

        return TypedResults.Ok(new NameSuggestions(nameKind, suggester.Suggest(nameKind, count ?? _defaultSuggestions)));
    }

    private static async Task<Results<Ok<NameCheckResult>, ValidationProblem>> CheckNames(
        NameCheckRequest request,
        INameGuard guard,
        CancellationToken cancellationToken)
    {
        var names = request.Names ?? [];
        if (names.Count is 0 or > NameCheckRequest.MaxNames)
        {
            return MatchCentreEndpoints.Invalid("names", $"Send 1 to {NameCheckRequest.MaxNames} names.");
        }

        // Format problems are answered here; only well-formed names go to the guard.
        var verdicts = new NameVerdict?[names.Count];
        var wellFormed = new List<(int Index, NameToCheck Name)>();
        for (var i = 0; i < names.Count; i++)
        {
            var path = string.Create(CultureInfo.InvariantCulture, $"names[{i}]");
            if (names[i] is not { } name)
            {
                return MatchCentreEndpoints.Invalid(path, "Each entry needs a kind and a name.");
            }

            if (!Enum.IsDefined(name.Kind))
            {
                return MatchCentreEndpoints.Invalid($"{path}.kind", "kind is team, venue, manager, player or abbreviation.");
            }

            if (NameFormat.Problem(name.Kind, name.Name) is { } problem)
            {
                verdicts[i] = new NameVerdict(name.Kind, name.Name ?? string.Empty, false, problem);
            }
            else
            {
                wellFormed.Add((i, name));
            }
        }

        var guarded = await guard.CheckAsync([.. wellFormed.Select(w => w.Name)], cancellationToken);
        for (var i = 0; i < wellFormed.Count; i++)
        {
            verdicts[wellFormed[i].Index] = guarded[i];
        }

        return TypedResults.Ok(new NameCheckResult([.. verdicts.Select(v => v!)]));
    }

    private static async Task<Results<Ok<IReadOnlyList<TeamSummary>>, ValidationProblem>> ListTeams(
        [FromQuery] string? origin,
        ITeamStore store,
        CancellationToken cancellationToken)
    {
        TeamOrigin? wanted = null;
        if (origin is not null)
        {
            if (EnumNames.Parse<TeamOrigin>(origin) is not { } parsed)
            {
                return MatchCentreEndpoints.Invalid("origin", "origin is builtin or fan.");
            }

            wanted = parsed;
        }

        var teams = await store.ListAsync(wanted, cancellationToken);
        return TypedResults.Ok<IReadOnlyList<TeamSummary>>([.. teams.Select(t => t.Detail.Team)]);
    }

    private static async Task<Results<Ok<TeamDetail>, ProblemHttpResult>> GetTeam(
        string teamId,
        ITeamStore store,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (await store.GetAsync(teamId, cancellationToken) is not { } team)
        {
            return NoTeam(teamId);
        }

        response.Headers.ETag = ETagFor(team.Detail.Version);
        return TypedResults.Ok(team.Detail);
    }

    private static async Task<Results<Created<TeamCreated>, ValidationProblem>> CreateTeam(
        TeamDraft draft,
        INameGuard guard,
        ITeamStore store,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (await ValidateAsync(draft, new HashSet<string>(), guard, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        var (key, hash) = EditKeys.Create();
        var detail = TeamViews.FromDraft(draft, TeamViews.NewTeamId(draft.Name), previous: null);
        await store.AddAsync(new StoredTeam(detail, hash), cancellationToken);

        response.Headers.ETag = ETagFor(detail.Version);
        return TypedResults.Created($"/api/teams/{Uri.EscapeDataString(detail.Team.Id)}", new TeamCreated(detail, key));
    }

    private static async Task<Results<Ok<TeamDetail>, ValidationProblem, ProblemHttpResult>> ReplaceTeam(
        string teamId,
        TeamDraft draft,
        [FromHeader(Name = EditKeys.Header)] string? editKey,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        INameGuard guard,
        ITeamStore store,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (await store.GetAsync(teamId, cancellationToken) is not { } current)
        {
            return NoTeam(teamId);
        }

        if (Forbidden(current, editKey) is { } forbidden)
        {
            return forbidden;
        }

        if (!IfMatchHolds(ifMatch, current.Detail.Version))
        {
            return Stale();
        }

        var existingIds = current.Detail.Squad.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        if (await ValidateAsync(draft, existingIds, guard, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        var detail = TeamViews.FromDraft(draft, teamId, current.Detail);
        if (!await store.ReplaceAsync(current with { Detail = detail }, current.Detail.Version, cancellationToken))
        {
            return Stale();
        }

        response.Headers.ETag = ETagFor(detail.Version);
        return TypedResults.Ok(detail);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteTeam(
        string teamId,
        [FromHeader(Name = EditKeys.Header)] string? editKey,
        ITeamStore store,
        IMatchLauncher launcher,
        CancellationToken cancellationToken)
    {
        if (await store.GetAsync(teamId, cancellationToken) is not { } current)
        {
            return NoTeam(teamId);
        }

        if (Forbidden(current, editKey) is { } forbidden)
        {
            return forbidden;
        }

        if (await launcher.IsTeamPlayingAsync(teamId, cancellationToken))
        {
            return TypedResults.Problem(title: "Team is playing", detail: "A team can't be deleted while it's in a match. Try again after full time.",
                statusCode: StatusCodes.Status409Conflict);
        }

        await store.DeleteAsync(teamId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> GetCrest(
        string teamId,
        ITeamStore store,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (await store.GetAsync(teamId, cancellationToken) is not { } team)
        {
            return NoTeam(teamId);
        }

        // The SVG is generated from validated parts, but it's served from the API's own origin,
        // so a strict policy makes sure nothing in it could ever run.
        response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";
        response.Headers.CacheControl = "public, max-age=300";
        response.Headers.ETag = ETagFor(team.Detail.Version);
        return TypedResults.Text(CrestSvg.Render(team.Detail.Crest), CrestSvg.ContentType);
    }

    private static async Task<Results<Accepted<MatchAccepted>, ValidationProblem, ProblemHttpResult>> StartMatch(
        MatchRequest request,
        [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
        ITeamStore store,
        IMatchLauncher launcher,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (idempotencyKey is { Length: 0 or > _maxIdempotencyKeyLength })
        {
            return MatchCentreEndpoints.Invalid(IdempotencyHeader, $"An idempotency key is 1 to {_maxIdempotencyKeyLength} characters.");
        }

        var missing = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.HomeTeamId))
        {
            missing["homeTeamId"] = ["The home team is required."];
        }

        if (string.IsNullOrWhiteSpace(request.AwayTeamId))
        {
            missing["awayTeamId"] = ["The away team is required."];
        }

        if (missing.Count > 0)
        {
            return TypedResults.ValidationProblem(missing);
        }

        if (request.HomeTeamId == request.AwayTeamId)
        {
            return TypedResults.Problem(title: "Same team twice", detail: "A team can't play itself.", statusCode: StatusCodes.Status409Conflict);
        }

        var home = await store.GetAsync(request.HomeTeamId, cancellationToken);
        var away = await store.GetAsync(request.AwayTeamId, cancellationToken);
        if (home is null || away is null)
        {
            return NoTeam(home is null ? request.HomeTeamId : request.AwayTeamId);
        }

        var problems = new Dictionary<string, string[]>();
        SheetProblems("home", request.Home, home, problems);
        SheetProblems("away", request.Away, away, problems);
        if (problems.Count > 0)
        {
            return TypedResults.ValidationProblem(problems);
        }

        var launch = await launcher.LaunchAsync(request, idempotencyKey, cancellationToken);
        switch (launch)
        {
            case { Outcome: LaunchOutcome.Accepted, Match: { } match }:
                return TypedResults.Accepted($"/api/matches/{Uri.EscapeDataString(match.MatchId)}", match);
            case { Outcome: LaunchOutcome.Busy }:
                var retryAfter = launch.RetryAfter ?? TimeSpan.FromMinutes(1);
                response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return TypedResults.Problem(title: "Too many matches", detail: launch.Reason, statusCode: StatusCodes.Status429TooManyRequests);
            default:
                return MatchCentreEndpoints.Unavailable(launch.Reason ?? "Matches can't be started right now.");
        }
    }

    private static void SheetProblems(string side, MatchdaySheet? sheet, StoredTeam team, Dictionary<string, string[]> problems)
    {
        if (sheet is null)
        {
            return;
        }

        var squad = team.Detail.Squad.ToDictionary(p => p.Id, p => p.Position, StringComparer.Ordinal);
        if (MatchdaySheetRules.Problems(sheet, squad) is { Count: > 0 } found)
        {
            problems[side] = [.. found];
        }
    }

    // Format and squad rules first, then the name guard, so the guard only sees well-formed names.
    private static async Task<ValidationProblem?> ValidateAsync(
        TeamDraft draft,
        IReadOnlySet<string> existingPlayerIds,
        INameGuard guard,
        CancellationToken cancellationToken)
    {
        var (problems, names) = TeamDraftValidator.Check(draft, existingPlayerIds);
        if (names.Count > 0)
        {
            var verdicts = await guard.CheckAsync([.. names.Select(n => n.Name)], cancellationToken);
            for (var i = 0; i < names.Count; i++)
            {
                if (!verdicts[i].Allowed)
                {
                    problems[names[i].Path] = [verdicts[i].Reason ?? "That name isn't allowed."];
                }
            }
        }

        return problems.Count > 0 ? TypedResults.ValidationProblem(problems) : null;
    }

    private static ProblemHttpResult? Forbidden(StoredTeam team, string? editKey) =>
        team.EditKeyHash is null
            ? TypedResults.Problem(title: "Read-only team", detail: "Built-in teams can't be changed. Build your own instead.", statusCode: StatusCodes.Status403Forbidden)
            : EditKeys.Matches(editKey, team.EditKeyHash)
                ? null
                : TypedResults.Problem(title: "Wrong edit key", detail: $"Send this team's edit key in the {EditKeys.Header} header.", statusCode: StatusCodes.Status403Forbidden);

    private static ProblemHttpResult Stale() =>
        TypedResults.Problem(title: "Team has changed", detail: "Someone saved this team since you loaded it. Reload it and try again.",
            statusCode: StatusCodes.Status412PreconditionFailed);

    private static ProblemHttpResult NoTeam(string teamId) => MatchCentreEndpoints.NotFound($"There's no club '{teamId}'.");

    private static string ETagFor(int version) => string.Create(CultureInfo.InvariantCulture, $"\"{version}\"");

    // RFC 9110: If-Match holds when it's absent, when it's "*" (the team exists, which the caller
    // has already checked), or when any tag in its list strongly matches. A weak tag (W/"1")
    // never matches, because If-Match uses strong comparison.
    private static bool IfMatchHolds(string? ifMatch, int version) =>
        ifMatch is null
        || ifMatch.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(tag => tag == "*" || tag == ETagFor(version));
}
