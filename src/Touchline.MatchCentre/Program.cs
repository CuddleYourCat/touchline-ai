var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var app = builder.Build();

// No UseHttpsRedirection: in Azure, Container Apps ingress terminates TLS and redirects HTTP to
// HTTPS before a request reaches the container, which listens on plain HTTP. Redirecting again
// in here would point clients at a port the container doesn't serve.
app.MapDefaultEndpoints();

app.MapGet("/api/status", () => TypedResults.Ok(new MatchCentreStatus("touchline-ai", "synthetic")));

await app.RunAsync();

/// <summary>
/// What the Match Centre reports about itself.
/// </summary>
/// <param name="Service">The service name.</param>
/// <param name="DataSource">Where match data comes from. Always <c>synthetic</c>, because every club, player and event is generated.</param>
internal sealed record MatchCentreStatus(string Service, string DataSource);

/// <summary>
/// The Match Centre entry point, made public so integration-style tests can host it in memory
/// with <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program;
