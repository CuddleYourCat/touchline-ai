using System.Net;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Demo;

namespace Touchline.MatchCentre.Storage;

/// <summary>
/// Routes each match to the source that serves it (ADR-036): <c>demo-*</c> IDs to the recording,
/// every other ID to Cosmos DB, when Cosmos DB is configured.
/// </summary>
internal static partial class MatchSources
{
    /// <summary>The service key of the recorded demo match's ports.</summary>
    public const string Demo = "demo";

    /// <summary>The service key of the Cosmos DB ports, and the name of its Aspire connection.</summary>
    public const string Cosmos = "cosmos";

    private const string _aspireSection = "Aspire:Microsoft:Azure:Cosmos";

    /// <summary>Whether the recording serves this match.</summary>
    /// <param name="matchId">The match.</param>
    /// <returns>True for a <c>demo-*</c> ID.</returns>
    public static bool IsDemo(string matchId) => matchId.StartsWith(DemoMatchSource.IdPrefix, StringComparison.Ordinal);

    /// <summary>The one routing rule: whether Cosmos DB, rather than the recording, serves this match.</summary>
    /// <param name="matchId">The match.</param>
    /// <param name="cosmosConfigured">Whether the Cosmos DB ports are registered.</param>
    /// <returns>True for a non-demo ID when Cosmos DB is configured.</returns>
    public static bool ServesFromCosmos(string matchId, bool cosmosConfigured) => cosmosConfigured && !IsDemo(matchId);

    /// <summary>
    /// Whether a failure is an outage of the source (Cosmos DB unreachable, throttled or timing
    /// out) rather than a bug. Only an outage becomes a <c>503</c>; anything else stays a <c>500</c>.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <param name="cancellationToken">The caller's token: its own cancellation is never an outage.</param>
    /// <returns>True for an infrastructure failure.</returns>
    public static bool IsOutage(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        MatchSourceUnavailableException => false,
        CosmosException cosmos => cosmos.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError,
        HttpRequestException or TimeoutException => true,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        _ => false,
    };

    /// <summary>
    /// Whether a <c>cosmos</c> connection is configured: a connection string, or the Aspire
    /// client's endpoint or connection string, as Container Apps sets it.
    /// </summary>
    /// <param name="configuration">The app's configuration.</param>
    /// <returns>True when the Cosmos DB read side should be registered.</returns>
    public static bool IsCosmosConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration.GetConnectionString(Cosmos))
        || new[] { _aspireSection, $"{_aspireSection}:{Cosmos}" }.Any(section =>
            !string.IsNullOrWhiteSpace(configuration[$"{section}:AccountEndpoint"])
            || !string.IsNullOrWhiteSpace(configuration[$"{section}:ConnectionString"]));

    /// <summary>
    /// Serves the Match Centre's ports through the routers, over the demo's keyed ports and,
    /// when registered, the Cosmos DB ones. A Cosmos DB failure becomes a <c>503</c> problem.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <returns>The services, for chaining.</returns>
    public static IServiceCollection AddMatchSourceRouting(this IServiceCollection services)
    {
        services.AddSingleton<IMatchReadModel>(provider => new RoutingMatchReadModel(
            provider.GetRequiredKeyedService<IMatchReadModel>(Demo),
            provider.GetKeyedService<IMatchReadModel>(Cosmos),
            provider.GetRequiredService<ILogger<RoutingMatchReadModel>>()));
        services.AddSingleton<ILiveFeed>(provider => new RoutingLiveFeed(
            provider.GetRequiredKeyedService<ILiveFeed>(Demo),
            provider.GetKeyedService<ILiveFeed>(Cosmos),
            provider.GetRequiredService<ILogger<RoutingLiveFeed>>()));
        services.AddExceptionHandler<SourceUnavailableHandler>();
        return services;
    }

    /// <summary>
    /// Runs a call against Cosmos DB, and turns an outage (see <see cref="IsOutage"/>) into a
    /// <see cref="MatchSourceUnavailableException"/>. Any other failure propagates, as a bug.
    /// </summary>
    /// <typeparam name="T">The call's result.</typeparam>
    /// <param name="call">The call.</param>
    /// <param name="what">What was asked for, for the log.</param>
    /// <param name="logger">Logs the failure.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The call's result.</returns>
    public static async Task<T> GuardAsync<T>(Func<Task<T>> call, string what, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            return await call();
        }
        catch (Exception exception) when (IsOutage(exception, cancellationToken))
        {
            LogUnavailable(logger, exception, what);
            throw new MatchSourceUnavailableException($"Cosmos DB couldn't serve {what}.", exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cosmos DB couldn't serve {What}")]
    internal static partial void LogUnavailable(ILogger logger, Exception exception, string what);

    /// <summary>Answers a Cosmos DB failure with a <c>503</c> problem instead of a <c>500</c>.</summary>
    private sealed class SourceUnavailableHandler : IExceptionHandler
    {
        /// <inheritdoc />
        public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            if (exception is not MatchSourceUnavailableException)
            {
                return false;
            }

            await MatchCentreEndpoints.Unavailable("This match's data isn't available right now. Try again shortly.").ExecuteAsync(httpContext);
            return true;
        }
    }
}

/// <summary>
/// Cosmos DB couldn't serve a match: it's unreachable, or its change feed hasn't started.
/// The API answers it with a <c>503</c> problem.
/// </summary>
/// <param name="message">What couldn't be served.</param>
/// <param name="innerException">The underlying failure.</param>
internal sealed class MatchSourceUnavailableException(string message, Exception innerException)
    : Exception(message, innerException);
