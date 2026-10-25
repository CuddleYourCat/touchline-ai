var builder = DistributedApplication.CreateBuilder(args);

// The event pipeline (ADR-004, ADR-023). Every Azure service runs as an emulator in Docker
// until the pipeline works end to end (ADR-016).
var eventHubs = builder.AddAzureEventHubs("eventhubs").RunAsEmulator();
var matchEvents = eventHubs.AddHub("match-events");

var serviceBus = builder.AddAzureServiceBus("servicebus").RunAsEmulator();
var keyMoments = serviceBus.AddServiceBusTopic("key-moments");
// Aspire resource names are unique across the whole app, and "commentary" is also a Cosmos DB
// container, so the subscriptions get prefixed resource names and keep their real names.
keyMoments.AddServiceBusSubscription("key-moments-commentary", subscriptionName: "commentary");
keyMoments.AddServiceBusSubscription("key-moments-highlights", subscriptionName: "highlights");

// A match reaching full time, which the recap function reads (ADR-030, amended 23 Oct). "recaps"
// is also a Cosmos DB container, so the subscription gets a prefixed resource name too.
var matchFinished = serviceBus.AddServiceBusTopic("match-finished");
matchFinished.AddServiceBusSubscription("match-finished-recaps", subscriptionName: "recaps");
// The search indexer's subscription (ADR-039); main.bicep creates it whether or not Search is deployed.
matchFinished.AddServiceBusSubscription("match-finished-search-index", subscriptionName: "search-index");

// The Cosmos DB emulator, with its Data Explorer, so stored events can be browsed locally.
var cosmos = builder.AddAzureCosmosDB("cosmos").RunAsEmulator(emulator => emulator.WithDataExplorer());
var database = cosmos.AddCosmosDatabase("touchline");
database.AddContainer("events", "/matchId");
database.AddContainer("matches", "/matchId");

// The season projection (ADR-025), and the change feed's leases (its checkpoints), which the
// Functions trigger requires to be partitioned by /id.
database.AddContainer("player-matches", "/playerId");
database.AddContainer("player-seasons", "/season");
database.AddContainer("leases", "/id");

// The agents' commentary, which the Match Centre follows on the change feed (ADR-029).
database.AddContainer("commentary", "/matchId");

// Each finished match's recaps, written once by the processor and read by the Match Centre.
database.AddContainer("recaps", "/matchId");

// Fan-built teams, written by the Match Centre (ADR-048). The emulator container has no default TTL,
// so a deleted team stays flagged here; in Azure the container's TTL removes it after a week.
database.AddContainer("teams", "/id");

// Fan match requests (ADR-050), written by the Match Centre and played by the simulator. Locally there's
// no Container Apps job to start, so the Match Centre's launcher stays unconnected (503) unless
// MatchCentre:FanMatches:JobResourceId is set; a request can still be played by running the simulator
// with Simulator__RequestId pointing at one written by hand in Data Explorer.
database.AddContainer("match-requests", "/id");

// The Functions host's own storage (AzureWebJobsStorage), which also holds the Event Hubs
// trigger's checkpoints.
var storage = builder.AddAzureStorage("storage").RunAsEmulator();

var eventProcessor = builder.AddAzureFunctionsProject<Projects.Touchline_EventProcessor>("eventprocessor")
    .WithHostStorage(storage)
    .WithReference(eventHubs).WaitFor(eventHubs)
    .WithReference(cosmos).WaitFor(cosmos)
    .WithReference(serviceBus).WaitFor(serviceBus)
    // The recap trigger has its own connection name (RecapFunction.ConnectionName), the same
    // namespace under a second name, as in main.bicep.
    .WithReference(serviceBus, connectionName: "servicebusrecaps")
    // The search indexer trigger (PossessionIndexFunction.ConnectionName), a third name for the same namespace.
    .WithReference(serviceBus, connectionName: "servicebussearch");

// The squad's model settings (ADR-043), passed through only when set in the AppHost's
// configuration (user secrets or environment), so a machine with no Foundry still runs the
// processor on template commentary and the Match Centre on the demo's stand-in answers. Both
// host the squad (ADR-030). No keys: the models are reached with an Entra ID token.
var squadSettings = new[] { "FoundryEndpoint", "ManagedIdentityClientId", "Deployments:Small", "Deployments:Large" }
    .Select(setting => (Name: $"Squad__{setting.Replace(":", "__", StringComparison.Ordinal)}", Value: builder.Configuration[$"Squad:{setting}"]))
    .Where(setting => !string.IsNullOrEmpty(setting.Value))
    .ToList();
foreach (var (name, value) in squadSettings)
{
    eventProcessor.WithEnvironment(name, value);
}

builder.AddProject<Projects.Touchline_Simulator>("simulator")
    .WithReference(matchEvents)
    .WithReference(cosmos)
    .WaitFor(eventHubs)
    .WaitFor(cosmos)
    .WaitFor(eventProcessor);

// The Match Centre's cache (ADR-061): verdicts, finished-match reads and the fan-match limits.
// Locally a Redis container; in Azure, Azure Managed Redis behind deployRedis.
var redis = builder.AddRedis("redis");

// No WaitFor(cosmos): the Match Centre must start, and serve its demo match, without the emulator.
// No WaitFor(redis) either: without Redis the caches stay in memory and the limits in Cosmos DB.
var matchcentre = builder.AddProject<Projects.Touchline_MatchCentre>("matchcentre")
    .WithReference(cosmos)
    .WithReference(redis)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");
foreach (var (name, value) in squadSettings)
{
    matchcentre.WithEnvironment(name, value);
}

// The team builder's name guard (ADR-049), only when MatchCentre:ContentSafety:Endpoint is set in the
// AppHost's user secrets; without it the Match Centre checks the name format alone. Needs a signed-in
// developer with Cognitive Services User on the account, and the blocklist loaded.
if (builder.Configuration["MatchCentre:ContentSafety:Endpoint"] is { Length: > 0 } contentSafetyEndpoint)
{
    matchcentre.WithEnvironment("MatchCentre__ContentSafety__Endpoint", contentSafetyEndpoint);
}

// The App Configuration emulator (ADR-062), only when AppConfiguration:Emulator is true in the AppHost's
// configuration. It starts empty, so the apps keep their appsettings defaults until a flag or value is added
// to it; WithReference passes both apps ConnectionStrings__appconfig, which they read in place of an endpoint.
if (string.Equals(builder.Configuration["AppConfiguration:Emulator"], "true", StringComparison.OrdinalIgnoreCase))
{
    var appConfig = builder.AddAzureAppConfiguration("appconfig").RunAsEmulator();
    eventProcessor.WithReference(appConfig);
    matchcentre.WithReference(appConfig);
}

// The Match Centre's React UI as a Vite dev server, for local development only: its proxy sends
// /api and /openapi to the Match Centre endpoint that WithReference injects. AddViteApp runs
// "npm install" first (a matchcentre-web-installer resource) and passes the port as --port.
// Production is unchanged: the Match Centre serves the built UI from its wwwroot (ADR-033).
builder.AddViteApp("matchcentre-web", "../Touchline.MatchCentre.Web")
    .WithReference(matchcentre)
    .WaitFor(matchcentre)
    .WithExternalHttpEndpoints();

await builder.Build().RunAsync();
