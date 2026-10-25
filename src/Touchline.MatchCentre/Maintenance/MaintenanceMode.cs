using System.Globalization;
using Microsoft.Extensions.Options;
using Touchline.MatchCentre.Api;

namespace Touchline.MatchCentre.Maintenance;

/// <summary>
/// Maintenance mode (ADR-060), as a short-circuit middleware: a page request gets the maintenance
/// page, and an API request gets a 503 problem, so a stream or the UI's fetches see an outage
/// rather than errors from half-deleted data. The health probes still answer, so Container Apps
/// keeps the replica in rotation and doesn't restart it.
/// </summary>
public static class MaintenanceMode
{
    /// <summary>The response header that marks a maintenance answer, which <c>deploy.ps1</c> checks for.</summary>
    public const string HeaderName = "X-Touchline-Maintenance";

    private static readonly string[] _probePaths = ["/health", "/alive"];
    private static readonly string[] _apiPrefixes = ["/api", "/openapi"];

    /// <summary>Binds and validates <see cref="MaintenanceOptions"/>.</summary>
    /// <param name="services">The services.</param>
    /// <returns>The services, for chaining.</returns>
    public static IServiceCollection AddMaintenanceMode(this IServiceCollection services)
    {
        services.AddOptions<MaintenanceOptions>()
            .BindConfiguration(MaintenanceOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        return services;
    }

    /// <summary>
    /// Adds the maintenance short-circuit. Call it early, before rate limiting and the endpoints,
    /// so nothing behind it runs while maintenance is on.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns>The application, for chaining.</returns>
    public static IApplicationBuilder UseMaintenanceMode(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<IOptions<MaintenanceOptions>>().Value;

        // The setting only changes with a new revision, so the check is made once, at startup.
        if (!options.Enabled)
        {
            return app;
        }

        return app.Use((context, next) => IsProbe(context.Request.Path)
            ? next(context)
            : RespondAsync(context, options));
    }

    private static bool IsProbe(PathString path) =>
        _probePaths.Any(probe => path.Equals(probe, StringComparison.OrdinalIgnoreCase));

    private static bool IsApi(PathString path) =>
        _apiPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    private static Task RespondAsync(HttpContext context, MaintenanceOptions options)
    {
        var response = context.Response;
        response.Headers[HeaderName] = "on";
        response.Headers.RetryAfter = options.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        response.Headers.CacheControl = "no-store";

        if (IsApi(context.Request.Path))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The Match Centre is under maintenance",
                detail: "The Match Centre is being refreshed. Try again in a few minutes.",
                type: ProblemTypes.Maintenance).ExecuteAsync(context);
        }

        response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        response.ContentType = "text/html; charset=utf-8";
        return response.WriteAsync(MaintenancePage.Html(options.RetryAfterSeconds, options.EstimatedMinutes), context.RequestAborted);
    }
}
