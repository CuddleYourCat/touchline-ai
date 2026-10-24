using Azure;
using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Touchline.Agents;
using Touchline.Search;

namespace Touchline.Tests.Unit.Search;

[TestFixture]
public sealed class PossessionIndexingTests
{
    private static PossessionDocument Document(int n) => new()
    {
        Id = PossessionDocument.KeyFor("m-1", n),
        MatchId = "m-1",
        PossessionId = n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Team = "Home",
        FromClock = "00:01",
        ToClock = "00:02",
        FromSeconds = 1,
        Players = ["A B"],
        EndedIn = "turnover",
        Summary = "text",
        SummaryVector = [0.1f]
    };

    private static Response<IndexDocumentsResult> Result(params bool[] succeeded) =>
        Response.FromValue(
            SearchModelFactory.IndexDocumentsResult(succeeded.Select((ok, i) =>
                SearchModelFactory.IndexingResult($"m-1_{i}", ok ? null : "rejected", ok, ok ? 200 : 400))),
            Mock.Of<Response>());

    [Test]
    public async Task Documents_are_merged_in_chunks_of_a_thousand()
    {
        var chunks = new List<int>();
        var client = new Mock<SearchClient>();
        client
            .Setup(c => c.MergeOrUploadDocumentsAsync(It.IsAny<IEnumerable<PossessionDocument>>(), It.IsAny<IndexDocumentsOptions?>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<PossessionDocument> docs, IndexDocumentsOptions? _, CancellationToken _) =>
            {
                chunks.Add(docs.Count());
                return Task.FromResult(Result(true));
            });

        await new AzurePossessionIndexer(client.Object).UpsertAsync([.. Enumerable.Range(0, 2300).Select(Document)], CancellationToken.None);

        chunks.Should().Equal(1000, 1000, 300);
    }

    [Test]
    public async Task Chaos_a_partial_failure_is_an_error_not_a_success()
    {
        var client = new Mock<SearchClient>();
        client
            .Setup(c => c.MergeOrUploadDocumentsAsync(It.IsAny<IEnumerable<PossessionDocument>>(), It.IsAny<IndexDocumentsOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result(true, false, true));

        var act = () => new AzurePossessionIndexer(client.Object).UpsertAsync([Document(0), Document(1), Document(2)], CancellationToken.None);

        (await act.Should().ThrowAsync<PossessionIndexingException>()).WithMessage("1 of 3*m-1_1*rejected*");
    }

    [Test]
    public void Without_a_Search_endpoint_only_the_null_indexer_is_registered()
    {
        using var provider = Build(new Dictionary<string, string?> { ["Squad:FoundryEndpoint"] = "https://foundry.example/" });

        provider.GetRequiredService<IPossessionIndexer>().Should().BeOfType<NullPossessionIndexer>();
        provider.GetService<IEmbeddingGenerator<string, Embedding<float>>>().Should().BeNull();
        provider.GetServices<IHostedService>().Should().ContainSingle(s => s.GetType().Name == "IndexingOffNotice");
    }

    [Test]
    public void Without_Foundry_there_is_nothing_to_embed_with_so_indexing_stays_off()
    {
        using var provider = Build(new Dictionary<string, string?> { ["Search:Endpoint"] = "https://search.example/" });

        provider.GetRequiredService<IPossessionIndexer>().Should().BeOfType<NullPossessionIndexer>();
        provider.GetService<IEmbeddingGenerator<string, Embedding<float>>>().Should().BeNull();
    }

    [Test]
    public void With_both_endpoints_the_Azure_indexer_and_the_embedding_generator_are_registered_keyless()
    {
        using var provider = Build(
            new Dictionary<string, string?>
            {
                ["Search:Endpoint"] = "https://search.example/",
                ["Squad:FoundryEndpoint"] = "https://foundry.example/"
            },
            services => services.AddSingleton(Mock.Of<TokenCredential>()));

        provider.GetRequiredService<IPossessionIndexer>().Should().BeOfType<AzurePossessionIndexer>().Which.Enabled.Should().BeTrue();
        provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>().Should().NotBeNull();
        provider.GetServices<IHostedService>().Should().NotContain(s => s.GetType().Name == "IndexingOffNotice");
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings, Action<IServiceCollection>? more = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<SquadOptions>().Bind(configuration.GetSection("Squad"));
        services.AddKeyedSingleton<IChatClient>(SquadModels.Small, (_, _) => Mock.Of<IChatClient>());
        more?.Invoke(services);
        services.AddPossessionIndexing(configuration);
        return services.BuildServiceProvider();
    }
}
