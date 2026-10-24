using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Azure;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Touchline.Search;

namespace Touchline.Tests.Unit.Search;

[TestFixture]
public sealed class AzurePossessionSearchTests
{
    private ServiceProvider _provider = null!;
    private ConcurrentQueue<string> _reasons = null!;
    private MeterListener _listener = null!;
    private Mock<IEmbeddingGenerator<string, Embedding<float>>> _embeddings = null!;
    private List<PossessionQuery> _requests = null!;

    [SetUp]
    public void SetUp()
    {
        _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        _reasons = new ConcurrentQueue<string>();
        _requests = [];
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument is { Meter.Name: SearchMetrics.MeterName, Name: SearchMetrics.FallbackCounterName })
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == SearchMetrics.ReasonTag && tag.Value is string reason)
                {
                    _reasons.Enqueue(reason);
                }
            }
        });
        _listener.Start();

        _embeddings = new Mock<IEmbeddingGenerator<string, Embedding<float>>>();
        _embeddings
            .Setup(e => e.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f })]));
    }

    [TearDown]
    public void TearDown()
    {
        _listener.Dispose();
        _provider.Dispose();
    }

    [Test]
    public async Task HappyPath_SendsHybridSemanticQueryFilteredToTheMatch()
    {
        var search = Create(new FakeTransport(_requests, (_, _) => Task.FromResult(Docs())));

        await search.SearchAsync("demo-3", "who scored the late winner", 5, CancellationToken.None);

        var request = _requests.Should().ContainSingle().Subject;
        request.Filter.Should().Be("matchId eq 'demo-3'");
        request.Text.Should().Be("who scored the late winner");
        request.Vector.Should().Equal(0.1f, 0.2f, 0.3f);
        request.SemanticConfiguration.Should().Be("possessions-semantic");
        request.Top.Should().Be(5);
        _reasons.Should().BeEmpty();
    }

    [Test]
    public async Task Question_NeverReachesTheFilter()
    {
        var search = Create(new FakeTransport(_requests, (_, _) => Task.FromResult(Docs())));

        await search.SearchAsync("demo-3", "x' or matchId ne 'y", 5, CancellationToken.None);

        _requests[0].Filter.Should().Be("matchId eq 'demo-3'");
        _requests[0].Text.Should().Be("x' or matchId ne 'y");
    }

    [Test]
    public void FilterFor_DoublesSingleQuotes()
    {
        AzurePossessionSearch.FilterFor("a'b''c").Should().Be("matchId eq 'a''b''''c'");
    }

    [TestCase("")]
    [TestCase("demo' or 1 eq 1 or matchId eq '")]
    [TestCase("demo 3")]
    public async Task UnsafeMatchId_ReturnsEmptyWithoutQuerying(string matchId)
    {
        var search = Create(new FakeTransport(_requests, (_, _) => Task.FromResult(Docs())));

        var hits = await search.SearchAsync(matchId, "goal", 5, CancellationToken.None);

        hits.Should().BeEmpty();
        _requests.Should().BeEmpty();
    }

    [TestCase(-3, 1)]
    [TestCase(0, 1)]
    [TestCase(500, AzurePossessionSearch.MaxTop)]
    public async Task Top_IsClamped(int top, int expected)
    {
        var search = Create(new FakeTransport(_requests, (_, _) => Task.FromResult(Docs())));

        await search.SearchAsync("demo-3", "goal", top, CancellationToken.None);

        _requests[0].Top.Should().Be(expected);
    }

    [Test]
    public async Task Results_AreMappedInReturnedOrderWithScores()
    {
        var search = Create(new FakeTransport(_requests, (_, _) => Task.FromResult(Docs(("demo-3_7", 7, 3.5), ("demo-3_2", 2, 1.25)))));

        var hits = await search.SearchAsync("demo-3", "goal", 5, CancellationToken.None);

        hits.Select(h => (h.Id, h.PossessionId, h.Score)).Should().Equal(("demo-3_7", 7, 3.5), ("demo-3_2", 2, 1.25));
        hits[0].Summary.Should().Be("A move.");
    }

    [Test]
    public async Task Results_AreCappedAtTop_AndAnUnreadableDocumentIsSkipped()
    {
        var bad = new ScoredDocument(Doc("demo-3_9", 9) with { FromClock = "nonsense" }, 9);
        var search = Create(new FakeTransport(_requests, (_, _) => Task.FromResult<IReadOnlyList<ScoredDocument>>(
            [bad, .. Docs(("demo-3_1", 1, 2), ("demo-3_2", 2, 1), ("demo-3_3", 3, 0.5))])));

        var hits = await search.SearchAsync("demo-3", "goal", 2, CancellationToken.None);

        hits.Select(h => h.Id).Should().Equal("demo-3_1", "demo-3_2");
    }

    [Test]
    public async Task EmbeddingFails_QueriesKeywordOnlyAndCounts()
    {
        _embeddings
            .Setup(e => e.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("down"));
        var search = Create(new FakeTransport(_requests, (_, _) => Task.FromResult(Docs())));

        var hits = await search.SearchAsync("demo-3", "goal", 5, CancellationToken.None);

        hits.Should().HaveCount(1);
        _requests.Should().ContainSingle().Which.Vector.Should().BeNull();
        _requests[0].SemanticConfiguration.Should().NotBeNull();
        _reasons.Should().Equal(SearchMetrics.EmbeddingFailed);
    }

    [Test]
    public async Task NoEmbedder_QueriesKeywordOnlyAndCountsNothing()
    {
        var search = new AzurePossessionSearch(
            new FakeTransport(_requests, (_, _) => Task.FromResult(Docs())),
            null,
            Options(),
            new SearchMetrics(_provider.GetRequiredService<IMeterFactory>()),
            NullLogger<AzurePossessionSearch>.Instance);

        await search.SearchAsync("demo-3", "goal", 5, CancellationToken.None);

        _requests[0].Vector.Should().BeNull();
        _reasons.Should().BeEmpty();
    }

    [Test]
    public async Task SlowEmbedding_FallsBackToKeywordOnlyInsideTheOverallBudget()
    {
        _embeddings
            .Setup(e => e.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions?>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<string>, EmbeddingGenerationOptions?, CancellationToken>(async (_, _, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new GeneratedEmbeddings<Embedding<float>>();
            });
        var search = Create(new FakeTransport(_requests, (_, _) => Task.FromResult(Docs())), seconds: 5);

        var hits = await search.SearchAsync("demo-3", "goal", 5, CancellationToken.None);

        hits.Should().HaveCount(1);
        _requests.Should().ContainSingle().Which.Vector.Should().BeNull();
        _reasons.Should().Equal(SearchMetrics.EmbeddingFailed);
    }

    [Test]
    public async Task Status503WithSemanticOff_IsNotRetried_AndCountsSearchUnavailable()
    {
        var search = Create(new FakeTransport(_requests, (q, _) => q.SemanticConfiguration is null
            ? throw new RequestFailedException(503, "down")
            : throw new RequestFailedException(429, "busy")));

        var hits = await search.SearchAsync("demo-3", "goal", 5, CancellationToken.None);

        hits.Should().BeEmpty();
        _requests.Should().HaveCount(2);
        _requests[1].SemanticConfiguration.Should().BeNull();
        _reasons.Should().Equal(SearchMetrics.SemanticUnavailable, SearchMetrics.SearchUnavailable);
    }

    [Test]
    public async Task SemanticFailureLateInTheBudget_IsNotRetried()
    {
        var search = Create(
            new FakeTransport(_requests, async (_, _) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400));
                throw new RequestFailedException(503, "slow then down");
            }),
            seconds: 0.5);

        var hits = await search.SearchAsync("demo-3", "goal", 5, CancellationToken.None);

        hits.Should().BeEmpty();
        _requests.Should().ContainSingle();
        _reasons.Should().Equal(SearchMetrics.SearchUnavailable);
    }

    [TestCase(429)]
    [TestCase(503)]
    public async Task SemanticRankerUnavailable_RetriesOnceWithoutSemantic(int status)
    {
        var search = Create(new FakeTransport(_requests, (q, _) =>
            q.SemanticConfiguration is null ? Task.FromResult(Docs()) : throw new RequestFailedException(status, "busy")));

        var hits = await search.SearchAsync("demo-3", "goal", 5, CancellationToken.None);

        hits.Should().HaveCount(1);
        _requests.Select(r => r.SemanticConfiguration is null).Should().Equal(false, true);
        _requests[1].Vector.Should().NotBeNull();
        _requests[1].Filter.Should().Be("matchId eq 'demo-3'");
        _reasons.Should().Equal(SearchMetrics.SemanticUnavailable);
    }

    [Test]
    public async Task OtherSearchFailure_ReturnsEmptyAndCounts()
    {
        var search = Create(new FakeTransport(_requests, (_, _) => throw new RequestFailedException(500, "boom")));

        var hits = await search.SearchAsync("demo-3", "goal", 5, CancellationToken.None);

        hits.Should().BeEmpty();
        _requests.Should().ContainSingle();
        _reasons.Should().Equal(SearchMetrics.SearchUnavailable);
    }

    [Test]
    public async Task NetworkFailure_ReturnsEmptyAndCounts()
    {
        var search = Create(new FakeTransport(_requests, (_, _) => throw new HttpRequestException("no route")));

        var hits = await search.SearchAsync("demo-3", "goal", 5, CancellationToken.None);

        hits.Should().BeEmpty();
        _reasons.Should().Equal(SearchMetrics.SearchUnavailable);
    }

    [Test]
    public async Task SlowSearch_TimesOutToAnEmptyList()
    {
        var search = Create(new FakeTransport(_requests, Hang), seconds: 0.3);

        var started = DateTime.UtcNow;
        var hits = await search.SearchAsync("demo-3", "goal", 5, CancellationToken.None);

        hits.Should().BeEmpty();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(2));
        _reasons.Should().Equal(SearchMetrics.Timeout);
    }

    [Test]
    public async Task CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var search = Create(new FakeTransport(_requests, Hang), seconds: 30);

        var pending = search.SearchAsync("demo-3", "goal", 5, cts.Token);
        await cts.CancelAsync();

        await pending.Invoking(p => p).Should().ThrowAsync<OperationCanceledException>();
        _reasons.Should().BeEmpty();
    }

    [Test]
    public async Task Chaos_SemanticFailsThenHybridHangs_ReturnsEmptyWithinTheBudget()
    {
        var search = Create(
            new FakeTransport(_requests, (q, t) => q.SemanticConfiguration is not null
                ? throw new RequestFailedException(503, "semantic down")
                : Hang(q, t)),
            seconds: 0.3);

        var started = DateTime.UtcNow;
        var hits = await search.SearchAsync("demo-3", "goal", 5, CancellationToken.None);

        hits.Should().BeEmpty();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(2));
        _requests.Should().HaveCount(2);
        _reasons.Should().Equal(SearchMetrics.SemanticUnavailable, SearchMetrics.Timeout);
    }

    private static Task<IReadOnlyList<ScoredDocument>> Hang(PossessionQuery query, CancellationToken token) =>
        Task.Delay(Timeout.Infinite, token).ContinueWith<IReadOnlyList<ScoredDocument>>(_ => [], token);

    private static PossessionDocument Doc(string id, int possessionId) => new()
    {
        Id = id,
        MatchId = "demo-3",
        PossessionId = possessionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Team = "Home",
        FromClock = "63:12",
        ToClock = "63:40",
        FromSeconds = 63 * 60 + 12,
        Players = ["Ada"],
        EndedIn = "goal",
        Summary = "A move."
    };

    private static IReadOnlyList<ScoredDocument> Docs(params (string Id, int Possession, double Score)[] items) =>
        items.Length == 0
            ? [new ScoredDocument(Doc("demo-3_1", 1), 1)]
            : [.. items.Select(i => new ScoredDocument(Doc(i.Id, i.Possession), i.Score))];

    private static IOptions<PossessionSearchOptions> Options(double seconds = 3) =>
        Microsoft.Extensions.Options.Options.Create(new PossessionSearchOptions { QueryTimeoutSeconds = seconds, EmbeddingTimeout = TimeSpan.FromMilliseconds(100) });

    private AzurePossessionSearch Create(IPossessionSearchTransport transport, double seconds = 3) =>
        new(
            transport,
            _embeddings.Object,
            Options(seconds),
            new SearchMetrics(_provider.GetRequiredService<IMeterFactory>()),
            NullLogger<AzurePossessionSearch>.Instance);

    private sealed class FakeTransport(List<PossessionQuery> requests, Func<PossessionQuery, CancellationToken, Task<IReadOnlyList<ScoredDocument>>> handler) : IPossessionSearchTransport
    {
        public Task<IReadOnlyList<ScoredDocument>> QueryAsync(PossessionQuery query, CancellationToken cancellationToken)
        {
            requests.Add(query);
            return handler(query, cancellationToken);
        }
    }
}
