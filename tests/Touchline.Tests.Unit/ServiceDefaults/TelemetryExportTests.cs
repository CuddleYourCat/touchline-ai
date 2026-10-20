using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;

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

    [Test]
    public void The_service_defaults_turn_on_the_Azure_SDK_spans()
    {
        Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings()).ConfigureOpenTelemetry();

        AppContext.TryGetSwitch(Extensions.AzureSdkActivitySourceSwitch, out var enabled).Should().BeTrue();
        enabled.Should().BeTrue("without it Cosmos DB, Service Bus and Event Hubs create no spans at all");
    }

    [Test]
    public void Service_Bus_sends_are_traced_once_the_service_defaults_come_first()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration["Aspire:Azure:Messaging:ServiceBus:FullyQualifiedNamespace"] = "touchline-test.servicebus.windows.net";

        // The order every service uses: the defaults, then the clients.
        builder.AddServiceDefaults();
        builder.AddAzureServiceBusClient("servicebus");

        using var host = builder.Build();
        host.Services.GetRequiredService<TracerProvider>();

        using var sender = new ActivitySource("Azure.Messaging.ServiceBus.ServiceBusSender");
        sender.HasListeners().Should().BeTrue("Aspire only adds the Service Bus sources when the switch is already on");
    }

    // Aspire listens to the Event Hubs sources with or without the switch. The switch matters for whether the
    // SDK creates the spans (an Azure.Core gate, shown for Cosmos DB), which needs a real hub to observe.
    [Test]
    public void Event_Hubs_sends_and_messages_are_listened_to()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration["Aspire:Azure:Messaging:EventHubs:EventHubProducerClient:FullyQualifiedNamespace"] = "touchline-test.servicebus.windows.net";
        builder.Configuration["Aspire:Azure:Messaging:EventHubs:EventHubProducerClient:EventHubName"] = "match-events";

        builder.AddServiceDefaults();
        builder.AddAzureEventHubProducerClient("match-events");

        using var host = builder.Build();
        host.Services.GetRequiredService<TracerProvider>();

        using var producer = new ActivitySource("Azure.Messaging.EventHubs.EventHubProducerClient");
        using var message = new ActivitySource("Azure.Messaging.EventHubs.Message");
        producer.HasListeners().Should().BeTrue("the simulator's sends are the first link in each match's trace");
        message.HasListeners().Should().BeTrue("each event's span carries the trace context into the message");
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
