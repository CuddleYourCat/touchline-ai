using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Touchline.Domain.Moments;
using Touchline.Domain.Teams;
using Touchline.EventProcessor.Processing;
using Touchline.Storage;

var builder = FunctionsApplication.CreateBuilder(args);

// OpenTelemetry through the service defaults, not a second Application Insights pipeline.
builder.AddServiceDefaults();

// Joins the worker's spans to the host's invocation trace, so a batch is one trace end to end.
builder.Services.AddOpenTelemetry().UseFunctionsWorkerDefaults();

// Clients for the store and the topic. Aspire supplies the connections (emulators locally).
builder.AddAzureCosmosClient("cosmos");
builder.AddAzureServiceBusClient("servicebus");

builder.Services.AddSingleton<KeyMomentDetector>(_ => new KeyMomentDetector());

// Each match's team profiles (ADR-050): the catalogue, plus a fan match's snapshots from its
// request in match-requests, read once per match. Registered before the commentary, recap and
// indexing services, which name players from it.
builder.Services.AddSingleton<IFanMatchRequestStore, CosmosFanMatchRequestStore>();
builder.Services.AddSingleton<IMatchTeamProfiles, FanMatchTeamProfiles>();
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

// The possession indexer on the same topic (ADR-039, ADR-057). Without Search:Endpoint it is a
// no-op that completes its messages.
builder.Services.AddPossessionIndexer(builder.Configuration);

await builder.Build().RunAsync();
