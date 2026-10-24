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
        options.MaxModelSummariesPerMatch.Should().Be(20);
        PossessionSearchOptions.SectionName.Should().Be("Search");
    }

    [Test]
    public void SemanticConfigurationName_MatchesTheIndexSchema()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "infra", "search", "possessions-index.json")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull();
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "infra", "search", "possessions-index.json")));
        var names = schema.RootElement.GetProperty("semantic").GetProperty("configurations").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString());

        names.Should().Contain(new PossessionSearchOptions().SemanticConfigurationName);
    }

    [Test]
    public void IsConfigured_FollowsTheEndpoint()
    {
        new PossessionSearchOptions().IsConfigured.Should().BeFalse();
        new PossessionSearchOptions { Endpoint = new Uri("https://example.search.windows.net") }.IsConfigured.Should().BeTrue();
    }
}
