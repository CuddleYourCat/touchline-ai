using System.Text.Json;

namespace Touchline.MatchCentre.Live;

/// <summary>
/// The documents a container has changed since the last poll (ADR-037). A seam, so the
/// change-feed reader is tested without Cosmos DB.
/// </summary>
internal interface IChangeFeedSource
{
    /// <summary>
    /// Reads what changed in a container since the previous call for it. The first call only
    /// marks "now", so it returns nothing that happened before it.
    /// </summary>
    /// <param name="container">The container's name, from <c>CosmosNames</c>.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The changed documents, oldest first within a partition; empty when nothing changed.</returns>
    Task<IReadOnlyList<JsonElement>> ReadChangesAsync(string container, CancellationToken cancellationToken);
}
