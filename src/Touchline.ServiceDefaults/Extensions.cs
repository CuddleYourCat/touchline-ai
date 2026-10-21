using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Cross-cutting defaults shared by every Touchline service: OpenTelemetry, health checks,
/// service discovery and HTTP resilience. See https://aka.ms/aspire/service-defaults.
/// </summary>
public static class Extensions
{
    private const string _healthEndpointPath = "/health";
    private const string _alivenessEndpointPath = "/alive";

    /// <summary>
    /// The <see cref="AppContext"/> switch that turns on the Azure SDKs' <c>ActivitySource</c> support, which
    /// OpenTelemetry needs to see their operations. <see cref="ConfigureOpenTelemetry{TBuilder}"/> sets it.
    /// </summary>
    internal const string AzureSdkActivitySourceSwitch = "Azure.Experimental.EnableActivitySource";

    /// <summary>The log category prefix every Azure SDK client library logs under, such as <c>Azure.Messaging.EventHubs</c>.</summary>
    internal const string AzureSdkLogCategory = "Azure";

    /// <summary>
    /// Adds OpenTelemetry, default health checks, service discovery and a standard
    /// resilience handler on every <see cref="System.Net.Http.HttpClient"/>.
    /// </summary>
    /// <typeparam name="TBuilder">The host builder type.</typeparam>
    /// <param name="builder">The host builder to configure.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        return builder;
    }

    /// <summary>
    /// Configures OpenTelemetry logging, metrics and tracing. They're exported over OTLP when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set (Aspire sets it locally), and to Application Insights
    /// through the Azure Monitor exporter when <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is set (in Azure).
    /// </summary>
    /// <remarks>
    /// Also turns on the Azure SDKs' OpenTelemetry spans (Cosmos DB, Service Bus, Event Hubs), which are still
    /// behind an experimental switch. Call this before registering any Azure client: Aspire's Service Bus and
    /// Event Hubs integrations only add their sources if the switch is already on.
    /// </remarks>
    /// <typeparam name="TBuilder">The host builder type.</typeparam>
    /// <param name="builder">The host builder to configure.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        // Without it the SDKs create no spans at all: no Cosmos DB operations (which in Direct mode don't
        // go over HTTP either), no Service Bus or Event Hubs sends, and no trace context in the messages.
        AppContext.SetSwitch(AzureSdkActivitySourceSwitch, true);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        // The Azure SDKs log every send and token request at Information, which duplicates their spans:
        // about 3 MB a match from Event Hubs alone, against a 100 MB daily cap (ADR-024). Warnings and
        // errors still come through, and a more specific category in configuration can lower it again.
        builder.Logging.AddFilter(AzureSdkLogCategory, LogLevel.Warning);

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                // The app's own meter is named after it, like its activity source below.
                metrics.AddMeter(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(_healthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(_alivenessEndpointPath)
                    )
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Still OpenTelemetry, with Azure Monitor as the destination: no Application Insights SDK.
        var useAzureMonitorExporter = !string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]);

        if (useAzureMonitorExporter)
        {
            builder.Services.AddOpenTelemetry().UseAzureMonitorExporter(options =>
            {
                // Keep every trace (ADR-024). The exporter's default rate limit, 5 new traces a second,
                // dropped most of the simulator's sends, because each starts its own trace. Volume is
                // watched against the daily cap instead.
                options.TracesPerSecond = null;
                options.SamplingRatio = 1.0F;
            });
        }

        return builder;
    }

    /// <summary>
    /// Adds a <c>self</c> liveness check tagged <c>live</c>.
    /// </summary>
    /// <typeparam name="TBuilder">The host builder type.</typeparam>
    /// <param name="builder">The host builder to configure.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Maps <c>/health</c> (readiness) and <c>/alive</c> (liveness) in every environment, because
    /// the AppHost and Container Apps probes call them in production too.
    /// </summary>
    /// <remarks>
    /// The default health check response is a bare status (<c>Healthy</c> or <c>Unhealthy</c>), with no
    /// check names, exception details or dependency information, so exposing it reveals nothing useful.
    /// Keep it that way: never add a detailed response writer to these endpoints.
    /// </remarks>
    /// <param name="app">The web application to configure.</param>
    /// <returns>The same application, for chaining.</returns>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(_healthEndpointPath);

        app.MapHealthChecks(_alivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live")
        });

        return app;
    }
}
