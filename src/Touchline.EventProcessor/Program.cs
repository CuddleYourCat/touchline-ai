using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Touchline.Domain.Moments;
using Touchline.EventProcessor.Processing;

var builder = FunctionsApplication.CreateBuilder(args);

// OpenTelemetry through the service defaults, not a second Application Insights pipeline.
builder.AddServiceDefaults();

// Joins the worker's spans to the host's invocation trace, so a batch is one trace end to end.
builder.Services.AddOpenTelemetry().UseFunctionsWorkerDefaults();

// Clients for the store and the topic. Aspire supplies the connections (emulators locally).
builder.AddAzureCosmosClient("cosmos");
builder.AddAzureServiceBusClient("servicebus");

builder.Services.AddSingleton<KeyMomentDetector>(_ => new KeyMomentDetector());
builder.Services.AddSingleton<PipelineTelemetry>();
builder.Services.AddSingleton<IMatchStore, CosmosMatchStore>();
builder.Services.AddSingleton<IKeyMomentPublisher, ServiceBusKeyMomentPublisher>();
builder.Services.AddSingleton<MatchEventProcessor>();

await builder.Build().RunAsync();
