using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Touchline.Domain;
using Touchline.Domain.Storage;
using Touchline.EventProcessor.Processing;

namespace Touchline.EventProcessor;

/// <summary>
/// The season projection's trigger (ADR-025): the <c>matches</c> container's change feed. A thin
/// adapter, so <see cref="SeasonProjector"/> is plain C# and tested directly.
/// </summary>
/// <param name="projector">Projects the finished fixtures.</param>
public sealed class ProjectSeason(SeasonProjector projector)
{
    /// <summary>The container whose change feed is read.</summary>
    public const string ContainerName = CosmosMatchStore.MatchesContainerName;

    /// <summary>The container holding the change feed's leases (its checkpoints).</summary>
    public const string LeaseContainerName = CosmosNames.Leases;

    /// <summary>
    /// The connection setting prefix. Locally Aspire supplies it; in Azure it's an identity-based
    /// connection (<c>cosmos__accountEndpoint</c>, with the processor's managed identity).
    /// </summary>
    public const string ConnectionName = "cosmos";

    /// <summary>
    /// Projects a batch of changed match documents. The documents arrive as a JSON array and are
    /// read with <see cref="DomainJson"/>, like everything else stored in Cosmos DB.
    /// </summary>
    /// <param name="documents">The changed documents, latest version of each.</param>
    /// <param name="cancellationToken">Stops the projection when the host shuts down.</param>
    /// <returns>A task that completes once the batch is projected.</returns>
    [Function(nameof(ProjectSeason))]
    [ExponentialBackoffRetry(5, "00:00:02", "00:00:30")]
    public Task RunAsync(
        [CosmosDBTrigger(
            CosmosMatchStore.DatabaseName,
            ContainerName,
            Connection = ConnectionName,
            LeaseContainerName = LeaseContainerName)] string documents,
        CancellationToken cancellationToken)
    {
        var changed = JsonSerializer.Deserialize<List<MatchRecord>>(documents, DomainJson.Options) ?? [];
        return projector.ProjectAsync(changed, cancellationToken);
    }
}
