using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Touchline.Domain.Matches;
using Touchline.Search;

namespace Touchline.Tests.Unit.Search;

[TestFixture]
public sealed class PossessionDocumentTests
{
    [Test]
    public void KeyFor_JoinsMatchAndPossessionWithAnUnderscore() =>
        PossessionDocument.KeyFor("demo-3", 17).Should().Be("demo-3_17");

    [Test]
    public void KeyFor_ProducesOnlyCharactersAnAzureSearchKeyAllows()
    {
        var key = PossessionDocument.KeyFor("Match_9=a-b", 0);

        key.Should().MatchRegex("^[A-Za-z0-9_=-]+$");
    }

    [TestCase("demo:3")]
    [TestCase("demo 3")]
    [TestCase("demo/3")]
    [TestCase("")]
    public void KeyFor_RejectsAMatchIdThatCannotBeAKey(string matchId)
    {
        var act = () => PossessionDocument.KeyFor(matchId, 1);

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void KeyFor_RejectsANegativePossessionId()
    {
        var act = () => PossessionDocument.KeyFor("demo-3", -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void JsonPropertyNames_MatchTheIndexSchemaFieldNamesExactly()
    {
        var schemaNames = IndexFieldNames();

        var documentNames = typeof(PossessionDocument)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .ToList();

        documentNames.Should().BeEquivalentTo(schemaNames);
    }

    [Test]
    public void Serialising_UsesTheSchemaNamesAndOmitsANullVector()
    {
        var json = JsonSerializer.Serialize(Sample(secondHalf: false));

        using var document = JsonDocument.Parse(json);
        document.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(IndexFieldNames().Where(n => n != "summaryVector"));
    }

    [Test]
    public void Serialising_WritesTheVectorAsAPlainArray()
    {
        var json = JsonSerializer.Serialize(Sample(secondHalf: false) with { SummaryVector = [0.5f, -0.25f] });

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("summaryVector").EnumerateArray().Select(e => e.GetSingle())
            .Should().Equal(0.5f, -0.25f);
    }

    [Test]
    public void ToHit_MapsAFirstHalfDocument()
    {
        var hit = Sample(secondHalf: false).ToHit(1.75);

        hit.Id.Should().Be("demo-3_17");
        hit.MatchId.Should().Be("demo-3");
        hit.PossessionId.Should().Be(17);
        hit.Team.Should().Be(TeamSide.Home);
        hit.From.Should().Be(MatchTime.At(MatchPeriod.FirstHalf, 12, 5));
        hit.To.Should().Be(MatchTime.At(MatchPeriod.FirstHalf, 12, 40));
        hit.Summary.Should().Be("A move.");
        hit.Score.Should().Be(1.75);
    }

    [Test]
    public void ToHit_ReadsASecondHalfDocumentFromItsOffsetSeconds()
    {
        var hit = Sample(secondHalf: true).ToHit(0.5);

        hit.From.Period.Should().Be(MatchPeriod.SecondHalf);
        hit.From.Should().Be(MatchTime.At(MatchPeriod.SecondHalf, 63, 12));
        hit.To.Should().Be(MatchTime.At(MatchPeriod.SecondHalf, 63, 40));
    }

    [Test]
    public void ToHit_RejectsAClockThatIsNotMinutesAndSeconds()
    {
        var act = () => (Sample(secondHalf: false) with { FromClock = "soon" }).ToHit(1);

        act.Should().Throw<FormatException>();
    }

    [Test]
    public void SecondsFor_OffsetsTheSecondHalfSoItSortsAfterFirstHalfAddedTime()
    {
        var addedTime = PossessionDocument.SecondsFor(MatchTime.At(MatchPeriod.FirstHalf, 47, 0));
        var restart = PossessionDocument.SecondsFor(MatchTime.At(MatchPeriod.SecondHalf, 45, 0));

        restart.Should().BeGreaterThan(addedTime);
        restart.Should().Be(3600 + 45 * 60);
    }

    private static PossessionDocument Sample(bool secondHalf)
    {
        var from = secondHalf ? MatchTime.At(MatchPeriod.SecondHalf, 63, 12) : MatchTime.At(MatchPeriod.FirstHalf, 12, 5);
        var to = secondHalf ? MatchTime.At(MatchPeriod.SecondHalf, 63, 40) : MatchTime.At(MatchPeriod.FirstHalf, 12, 40);
        return new PossessionDocument
        {
            Id = PossessionDocument.KeyFor("demo-3", 17),
            MatchId = "demo-3",
            PossessionId = "17",
            Team = nameof(TeamSide.Home),
            FromClock = from.ToString(),
            ToClock = to.ToString(),
            FromSeconds = PossessionDocument.SecondsFor(from),
            Players = ["A Player", "B Player"],
            EndedIn = "turnover",
            Summary = "A move."
        };
    }

    private static List<string> IndexFieldNames()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "infra", "search", "possessions-index.json")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the repo's infra/search/possessions-index.json must be reachable from the test output");
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "infra", "search", "possessions-index.json")));
        return schema.RootElement.GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("name").GetString()!)
            .ToList();
    }
}
