using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

using Touchline.MatchCentre.TeamBuilding;

namespace Touchline.MatchCentre.Api;

/// <summary>
/// Per-client request limits. Bound from <c>MatchCentre:RateLimits</c>.
/// </summary>
public sealed class RateLimitSettings
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "MatchCentre:RateLimits";

    /// <summary>How many teams one client may create in an hour (ADR-031).</summary>
    [Range(1, 10_000)]
    public int TeamsPerHour { get; set; } = 10;

    /// <summary>
    /// How many requests one client may make in a minute to endpoints that call a model or
    /// Content Safety: fan questions, recaps and name checks. Each costs money.
    /// </summary>
    [Range(1, 10_000)]
    public int PaidCallsPerMinute { get; set; } = 10;
}

/// <summary>
/// Registers the rate limits, and the team builder's name suggester.
/// </summary>
internal static class RateLimiting
{
    /// <summary>The policy on team creation.</summary>
    public const string TeamCreation = "team-creation";

    /// <summary>The policy on endpoints that call a model or Content Safety.</summary>
    public const string PaidCalls = "paid-calls";

    /// <summary>Adds the settings, both policies and the name suggester.</summary>
    /// <param name="services">The services.</param>
    /// <returns>The services, for chaining.</returns>
    public static IServiceCollection AddMatchCentreRateLimits(this IServiceCollection services)
    {
        services.AddOptions<RateLimitSettings>()
            .BindConfiguration(RateLimitSettings.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(new NameSuggester(new Random()));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(TeamCreation, context => PerClient(context, s => s.TeamsPerHour, TimeSpan.FromHours(1)));
            limiter.AddPolicy(PaidCalls, context => PerClient(context, s => s.PaidCallsPerMinute, TimeSpan.FromMinutes(1)));
            limiter.OnRejected = async (rejected, _) =>
            {
                if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    rejected.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                await TypedResults.Problem(
                    title: "Too many requests",
                    detail: "That's a lot of requests in a short time. Wait a moment and try again.",
                    statusCode: StatusCodes.Status429TooManyRequests).ExecuteAsync(rejected.HttpContext);
            };
        });

        return services;
    }

    private static RateLimitPartition<string> PerClient(HttpContext context, Func<RateLimitSettings, int> limit, TimeSpan window)
    {
        var permits = limit(context.RequestServices.GetRequiredService<IOptions<RateLimitSettings>>().Value);
        return RateLimitPartition.GetFixedWindowLimiter(ClientAddress(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            QueueLimit = 0
        });
    }

    // Container Apps' ingress appends the caller's address to X-Forwarded-For, so only the
    // rightmost entry can be trusted: anything to its left came from the client.
    private static string ClientAddress(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        var rightmost = forwarded.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return rightmost ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
