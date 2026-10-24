namespace Touchline.MatchCentre.Api;

/// <summary>
/// The header for a response served without one of its sources. The router reports the gap in
/// <see cref="MatchList.Degraded"/>, and the endpoint turns it into the <see cref="Header"/>
/// response header, so the response body keeps its shape.
/// </summary>
internal static class DegradedSources
{
    /// <summary>The response header that names the sources a response was served without.</summary>
    public const string Header = "Touchline-Degraded";

    /// <summary>The header's value when Cosmos DB was down.</summary>
    public const string Cosmos = "cosmos";
}
