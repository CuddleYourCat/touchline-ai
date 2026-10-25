using Microsoft.Extensions.Options;
using Touchline.Simulator.Publishing;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// The producer for the match-events hub. Aspire supplies the connection (the emulator locally).
builder.AddAzureEventHubProducerClient("match-events");

// Read-only for the season's bans before a fixture (ADR-025); read and status updates for a fan
// match request (ADR-050).
builder.AddAzureCosmosClient("cosmos");

// Every configured match is checked at startup, before anything is published.
builder.Services.AddOptions<SimulatorOptions>()
    .Bind(builder.Configuration.GetSection(SimulatorOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<SimulatorOptions>, SimulatorOptionsValidator>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IMatchEventSender, EventHubsMatchEventSender>();
builder.Services.AddSingleton<ISeasonReader, CosmosSeasonReader>();

// A fan match request (ADR-050), read and moved on when Simulator:RequestId is set. The identity
// holds Data Contributor on the match-requests container only.
builder.Services.AddSingleton<Touchline.Storage.IFanMatchRequestStore, Touchline.Storage.CosmosFanMatchRequestStore>();
builder.Services.AddHostedService<MatchPublisher>();

var host = builder.Build();
await host.RunAsync();
