var builder = DistributedApplication.CreateBuilder(args);

// The event pipeline (ADR-004, ADR-023). Every Azure service runs as an emulator in Docker
// until the pipeline works end to end (ADR-016).
var eventHubs = builder.AddAzureEventHubs("eventhubs").RunAsEmulator();
var matchEvents = eventHubs.AddHub("match-events");

var serviceBus = builder.AddAzureServiceBus("servicebus").RunAsEmulator();
var keyMoments = serviceBus.AddServiceBusTopic("key-moments");
keyMoments.AddServiceBusSubscription("commentary");
keyMoments.AddServiceBusSubscription("highlights");

// The Cosmos DB emulator, with its Data Explorer, so stored events can be browsed locally.
var cosmos = builder.AddAzureCosmosDB("cosmos").RunAsEmulator(emulator => emulator.WithDataExplorer());
var database = cosmos.AddCosmosDatabase("touchline");
database.AddContainer("events", "/matchId");
database.AddContainer("matches", "/matchId");

// The Functions host's own storage (AzureWebJobsStorage), which also holds the Event Hubs
// trigger's checkpoints.
var storage = builder.AddAzureStorage("storage").RunAsEmulator();

var eventProcessor = builder.AddAzureFunctionsProject<Projects.Touchline_EventProcessor>("eventprocessor")
    .WithHostStorage(storage)
    .WithReference(eventHubs).WaitFor(eventHubs)
    .WithReference(cosmos).WaitFor(cosmos)
    .WithReference(serviceBus).WaitFor(serviceBus);

builder.AddProject<Projects.Touchline_Simulator>("simulator")
    .WithReference(matchEvents)
    .WaitFor(eventHubs)
    .WaitFor(eventProcessor);

builder.AddProject<Projects.Touchline_MatchCentre>("matchcentre")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

await builder.Build().RunAsync();
