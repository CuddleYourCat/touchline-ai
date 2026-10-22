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

await app.RunAsync();

/// <summary>
/// The Match Centre entry point, made public so integration-style tests can host it in memory
/// with <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program;
