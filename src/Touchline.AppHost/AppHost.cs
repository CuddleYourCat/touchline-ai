var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Touchline_Simulator>("simulator");

builder.AddProject<Projects.Touchline_MatchCentre>("matchcentre")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

await builder.Build().RunAsync();
