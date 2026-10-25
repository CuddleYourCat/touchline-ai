using Microsoft.Azure.AppConfiguration.AspNetCore;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Touchline.Configuration;

namespace Touchline.MatchCentre.Configuration;

/// <summary>Wires Azure App Configuration and feature flags into the Match Centre (ADR-062).</summary>
public static class MatchCentreAppConfiguration
{
    /// <summary>
    /// Adds the App Configuration provider when <c>AppConfiguration:Endpoint</c> is set, plus feature flags and live
    /// squad options. Call it straight after creating the builder, before anything reads configuration, so the
    /// store's values are in place for the rest of the registrations.
    /// </summary>
    /// <param name="builder">The web application builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static WebApplicationBuilder AddMatchCentreAppConfiguration(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Configuration.AddTouchlineAppConfiguration())
        {
            builder.Services.AddAzureAppConfiguration();
        }

        builder.Services.AddTouchlineFeatureFlags();
        return builder;
    }

    /// <summary>
    /// Adds the refresh middleware when the provider was added: each request nudges the provider, which checks the
    /// sentinel and the flags once the 15 minutes are up, without holding the request. With no store it adds nothing.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns>The application, for chaining.</returns>
    public static WebApplication UseMatchCentreAppConfiguration(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (app.Services.GetService<IConfigurationRefresherProvider>() is not null)
        {
            // Only API calls nudge the provider. The container's /alive and /health probes, static files and the
            // long-lived stream would otherwise keep triggering checks, and the Free tier counts every one (ADR-062).
            app.UseWhen(context => TriggersRefresh(context.Request.Path), branch => branch.UseAzureAppConfiguration());
        }

        return app;
    }

    /// <summary>Whether a request may trigger a refresh check: an API call other than the stream.</summary>
    /// <param name="path">The request path.</param>
    /// <returns>True for <c>/api</c> paths, except a match's <c>/stream</c>.</returns>
    internal static bool TriggersRefresh(PathString path) =>
        path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        && !path.Value!.EndsWith("/stream", StringComparison.OrdinalIgnoreCase);
}
