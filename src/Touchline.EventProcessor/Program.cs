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
builder.Services.AddSingleton<IMatchFinishedPublisher, ServiceBusMatchFinishedPublisher>();
builder.Services.AddSingleton<MatchEventProcessor>();

// The season projection, fed by the matches container's change feed (ADR-025).
builder.Services.AddSingleton<ISeasonStore, CosmosSeasonStore>();
builder.Services.AddSingleton<SeasonProjector>();

// The live commentary on the key-moments topic (ADR-030). Without Squad:FoundryEndpoint the
// models are stand-ins that always fail, so every moment gets its template lines.
builder.Services.AddLiveCommentary(builder.Configuration);

// The full-time recaps on the match-finished topic (ADR-030, amended 23 Oct). Without models it
// writes nothing, and the Match Centre serves the template recap.
builder.Services.AddFullTimeRecaps();

await builder.Build().RunAsync();
