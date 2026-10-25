using System.Text.Json.Serialization;
using Touchline.Domain.Moments;
using Touchline.Domain.Teams;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Caching;
using Touchline.MatchCentre.Demo;
using Touchline.MatchCentre.Maintenance;
using Touchline.MatchCentre.Squad;
using Touchline.MatchCentre.Storage;
using Touchline.MatchCentre.TeamBuilding;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Enums travel as names, as everywhere else (DomainJson), and numbers only as numbers, for
// responses, stream messages and the OpenAPI document's schemas alike. The web defaults also
// accept numbers written as strings, which would make every number "integer or string" in the
// contract, and in the TypeScript generated from it.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<StreamMessageSchemas>());

builder.Services.AddOptions<StreamOptions>()
    .BindConfiguration(StreamOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddMaintenanceMode();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(TeamProfileCatalog.LoadEmbedded());
builder.Services.AddMatchCentreRateLimits();
builder.Services.AddSingleton(provider =>
    new KeyMomentDetector(teams: provider.GetRequiredService<IReadOnlyDictionary<string, TeamProfile>>().Values));
builder.Services.AddSingleton(provider => new NotableMomentDetector(
    provider.GetRequiredService<KeyMomentDetector>(),
    provider.GetRequiredService<IReadOnlyDictionary<string, TeamProfile>>().Values));

// Matches are routed by ID (ADR-036): demo-* IDs from the recording, every other ID from Cosmos DB
// when a cosmos connection is configured. Without one (tests, a plain dotnet run) the app is
// demo-only. Cosmos DB stays out of both health checks, so an outage never restarts a replica or
// takes it out of rotation; the routers answer its IDs with a 503 instead.
builder.Services.AddDemoMatch();
if (MatchSources.IsCosmosConfigured(builder.Configuration))
{
    builder.AddCosmosReadModel();
    builder.AddCosmosLiveFeed();
}

builder.Services.AddMatchSourceRouting();
builder.AddTeamBuilderBackends();

// The caches (ADR-061): finished-match reads, name-guard verdicts and, with Redis, the fan-match limits.
// After the team builder, whose Cosmos DB limit store it wraps.
builder.AddTouchlineCaching();
builder.Services.AddDemoTeamBuilder();

// The Analyst answers fan questions (ADR-030) when Squad:FoundryEndpoint is set; without it the
// demo squad's stand-in answers stay. After the read model and the team store, which its tools use.
builder.Services.AddMatchCentreAnalyst(builder.Configuration);

var app = builder.Build();

// No UseHttpsRedirection: in Azure, Container Apps ingress terminates TLS and redirects HTTP to
// HTTPS before a request reaches the container, which listens on plain HTTP. Redirecting again
// in here would point clients at a port the container doesn't serve.
app.UseExceptionHandler();

// Maintenance mode (ADR-060) answers before anything else runs, apart from the health probes.
app.UseMaintenanceMode();
app.UseStatusCodePages();
app.UseRateLimiter();

// After the rate limiter, so a cached recap still counts against PaidCalls, as it did before (ADR-061).
app.UseOutputCache();

app.MapDefaultEndpoints();
app.MapOpenApi();
app.MapMatchCentreApi();

// The Match Centre UI (ADR-033): the built React app in wwwroot, mapped after the API. Any other
// path that isn't a file gets index.html, so the browser router can handle /matches/{id} and the
// rest. /api and /openapi are left out of the fallback, so an unknown API path still gets a 404
// problem from UseStatusCodePages, never a page of HTML. With no build in wwwroot (tests, a plain
// dotnet run beside the Vite dev server) the fallback finds no file and answers 404. The
// exclusion ignores case, so /API/foo is treated like /api/foo. index.html goes out with
// Cache-Control: no-cache, so a browser always revalidates it after a deploy and never keeps an
// old copy pointing at hashed bundles the new revision no longer has; the fingerprinted bundles
// themselves stay cacheable.
app.MapStaticAssets();
var indexHtmlOptions = new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
};
app.MapFallbackToFile("/", "index.html", indexHtmlOptions);
app.MapFallbackToFile("{*path:nonfile:regex(^(?i)(?!(api|openapi)(/|$)).*$)}", "index.html", indexHtmlOptions);

await app.RunAsync();

/// <summary>
/// The Match Centre entry point, made public so integration-style tests can host it in memory
/// with <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program;
