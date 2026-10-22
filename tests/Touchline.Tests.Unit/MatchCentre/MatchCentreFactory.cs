using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Touchline.Tests.Unit.MatchCentre;

/// <summary>
/// Hosts the Match Centre in memory with demo settings for a test.
/// </summary>
internal static class MatchCentreFactory
{
    /// <summary>
    /// A match so fast it's over within a few milliseconds, and a gap so long that lap 1 stays
    /// the finished match for the whole test run.
    /// </summary>
    public static WebApplicationFactory<Program> Finished() => Create(speed: "100000", lapGap: "01:00:00");

    /// <summary>A match so slow that nothing after kick-off happens during a test.</summary>
    public static WebApplicationFactory<Program> Live() => Create(speed: "0.1", lapGap: "00:00:00");

    public static WebApplicationFactory<Program> Create(string speed, string lapGap, string heartbeat = "00:00:00.050", string teamsPerHour = "1000", string paidCallsPerMinute = "1000") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseEnvironment("Production")
            .UseSetting("MatchCentre:Demo:Speed", speed)
            .UseSetting("MatchCentre:Demo:LapGap", lapGap)
            .UseSetting("MatchCentre:Stream:HeartbeatInterval", heartbeat)
            .UseSetting("MatchCentre:RateLimits:TeamsPerHour", teamsPerHour)
            .UseSetting("MatchCentre:RateLimits:PaidCallsPerMinute", paidCallsPerMinute));
}
