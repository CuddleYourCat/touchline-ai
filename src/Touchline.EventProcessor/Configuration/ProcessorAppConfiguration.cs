using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Touchline.Configuration;

namespace Touchline.EventProcessor.Configuration;

/// <summary>Wires Azure App Configuration and feature flags into the Event Processor (ADR-062).</summary>
public static class ProcessorAppConfiguration
{
    /// <summary>
    /// Adds the App Configuration provider when <c>AppConfiguration:Endpoint</c> is set, plus feature flags and live
    /// squad options. Call it first, before anything reads configuration.
    /// </summary>
    /// <remarks>
    /// A Functions isolated worker has no web request to hang a refresh on, so the Functions middleware from
    /// <c>Microsoft.Azure.AppConfiguration.Functions.Worker</c> asks the provider to refresh at the start of each
    /// invocation. The provider only checks the store once the 15 minutes are up, so a busy trigger doesn't
    /// spend the Free tier's daily requests.
    /// </remarks>
    /// <param name="builder">The Functions application builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static FunctionsApplicationBuilder AddProcessorAppConfiguration(this FunctionsApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Configuration.AddTouchlineAppConfiguration())
        {
            builder.Services.AddAzureAppConfiguration();
            builder.UseAzureAppConfiguration();
        }

        builder.Services.AddTouchlineFeatureFlags();
        return builder;
    }
}
