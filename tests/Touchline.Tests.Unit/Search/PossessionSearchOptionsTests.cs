using System.Text.Json;
using FluentAssertions;
using Touchline.Search;

namespace Touchline.Tests.Unit.Search;

[TestFixture]
public sealed class PossessionSearchOptionsTests
{
    [Test]
    public void Defaults_FollowTheAdrs()
    {
        var options = new PossessionSearchOptions();

        options.Endpoint.Should().BeNull();
        options.IndexName.Should().Be("possessions");
        options.EmbeddingDeployment.Should().Be("text-embedding-3-small");
        options.QueryTimeoutSeconds.Should().Be(3);
        options.EmbeddingTimeout.Should().Be(TimeSpan.FromSeconds(1));
        options.MaxQueryCharacters.Should().Be(500);
        options.MaxModelSummariesPerMatch.Should().Be(20);
        PossessionSearchOptions.SectionName.Should().Be("Search");
    }

    [Test]
    public void SemanticConfigurationName_MatchesTheIndexSchema()
    {
        using var schema = LoadIndexSchema();
        var names = schema.RootElement.GetProperty("semantic").GetProperty("configurations").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString());

        names.Should().Contain(new PossessionSearchOptions().SemanticConfigurationName);
    }

    [Test]
    public void SemanticConfiguration_UsesTheRestApiPropertyNames()
    {
        // The REST API (2024-07-01) names these prioritizedContentFields and prioritizedKeywordsFields;
        // contentFields and keywordsFields are the .NET SDK's names, and the service rejects them.
        using var schema = LoadIndexSchema();
        var searchable = schema.RootElement.GetProperty("fields").EnumerateArray()
            .Where(f => f.TryGetProperty("searchable", out var s) && s.GetBoolean())
            .Select(f => f.GetProperty("name").GetString())
            .ToHashSet();
        var prioritized = schema.RootElement.GetProperty("semantic").GetProperty("configurations")[0].GetProperty("prioritizedFields");

        prioritized.EnumerateObject().Select(p => p.Name).Should()
            .BeSubsetOf(["titleField", "prioritizedContentFields", "prioritizedKeywordsFields"]);
        prioritized.GetProperty("prioritizedContentFields").EnumerateArray()
            .Concat(prioritized.GetProperty("prioritizedKeywordsFields").EnumerateArray())
            .Select(f => f.GetProperty("fieldName").GetString())
            .Should().OnlyContain(name => searchable.Contains(name));
    }

    private static JsonDocument LoadIndexSchema()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "infra", "search", "possessions-index.json")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull();
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "infra", "search", "possessions-index.json")));
    }

    [Test]
    public void IsConfigured_FollowsTheEndpoint()
    {
        new PossessionSearchOptions().IsConfigured.Should().BeFalse();
        new PossessionSearchOptions { Endpoint = new Uri("https://example.search.windows.net") }.IsConfigured.Should().BeTrue();
    }
}
