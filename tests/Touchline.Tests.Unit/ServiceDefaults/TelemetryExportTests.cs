using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Touchline.Tests.Unit.ServiceDefaults;

/// <summary>Where the service defaults send telemetry (ADR-024): OTLP locally, Azure Monitor in Azure.</summary>
[TestFixture]
public class TelemetryExportTests
{
    private const string _connectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://localhost/";

    [Test]
    public void Telemetry_goes_to_Azure_Monitor_when_Application_Insights_is_configured()
    {
        AzureMonitorRegistered(_connectionString).Should().BeTrue("in Azure this is the only way any telemetry reaches Application Insights");
    }

    [Test]
    public void Locally_nothing_is_sent_to_Azure_Monitor()
    {
        AzureMonitorRegistered(connectionString: null).Should().BeFalse("under Aspire, the OTLP endpoint is the only destination");
    }

    // The service registrations are inspected, not run, so nothing is ever exported.
    private static bool AzureMonitorRegistered(string? connectionString)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        if (connectionString is not null)
        {
            builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] = connectionString;
        }

        builder.ConfigureOpenTelemetry();

        return builder.Services.Any(service => FromAzureMonitor(service.ServiceType) || FromAzureMonitor(service.ImplementationType));
    }

    private static bool FromAzureMonitor(Type? type) =>
        type is not null
        && (type.Assembly.GetName().Name == "Azure.Monitor.OpenTelemetry.Exporter"
            || type.GenericTypeArguments.Any(argument => FromAzureMonitor(argument)));
}
