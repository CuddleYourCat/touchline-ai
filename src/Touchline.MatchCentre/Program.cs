using System.Text.Json.Serialization;
using Touchline.Domain.Moments;
using Touchline.Domain.Teams;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Demo;
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

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(TeamProfileCatalog.LoadEmbedded());
builder.Services.AddMatchCentreRateLimits();
builder.Services.AddSingleton(provider =>
    new KeyMomentDetector(teams: provider.GetRequiredService<IReadOnlyDictionary<string, TeamProfile>>().Values));

// The recorded match behind every port, until the live read side (ADR-029) and the agents
// (ADR-030) arrive. Those lanes register their own implementations instead of this.
builder.Services.AddDemoMatch();
builder.Services.AddDemoTeamBuilder();

var app = builder.Build();

// No UseHttpsRedirection: in Azure, Container Apps ingress terminates TLS and redirects HTTP to
// HTTPS before a request reaches the container, which listens on plain HTTP. Redirecting again
// in here would point clients at a port the container doesn't serve.
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();

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
