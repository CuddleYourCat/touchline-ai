using Microsoft.Extensions.Options;
using Touchline.Simulator.Publishing;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// The producer for the match-events hub. Aspire supplies the connection (the emulator locally).
builder.AddAzureEventHubProducerClient("match-events");

// Read-only, for the season's bans before a fixture (ADR-025).
builder.AddAzureCosmosClient("cosmos");

// Every configured match is checked at startup, before anything is published.
builder.Services.AddOptions<SimulatorOptions>()
    .Bind(builder.Configuration.GetSection(SimulatorOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<SimulatorOptions>, SimulatorOptionsValidator>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IMatchEventSender, EventHubsMatchEventSender>();
builder.Services.AddSingleton<ISeasonReader, CosmosSeasonReader>();
builder.Services.AddHostedService<MatchPublisher>();

var host = builder.Build();
await host.RunAsync();
