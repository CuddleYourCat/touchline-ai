using System.Text.Json;
using FluentAssertions;
using Touchline.MatchCentre.Api;

namespace Touchline.Tests.Unit.MatchCentre;

[TestFixture]
public class OpenApiContractTests
{
    private static readonly string[] _contractPath = ["contracts", "matchcentre.openapi.json"];

    [Test]
    public async Task The_committed_contract_matches_the_document_the_service_generates()
    {
        using var factory = MatchCentreFactory.Finished();
        using var client = factory.CreateClient();

        var generated = Normalise(await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative)));
        var path = Snapshots.PathOf(_contractPath);

        if (Snapshots.Updating)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, generated);
            Assert.Pass("The OpenAPI contract was regenerated.");
        }

        Normalise(await File.ReadAllTextAsync(path)).Should().Be(generated, Snapshots.StaleMessage("contracts/matchcentre.openapi.json"));
    }

    [Test]
    public async Task The_contract_describes_every_stream_message()
    {
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(Snapshots.PathOf(_contractPath)));

        var schemas = contract.RootElement.GetProperty("components").GetProperty("schemas");

        StreamMessageSchemas.Types.Select(t => t.Name).Where(name => !schemas.TryGetProperty(name, out _)).Should().BeEmpty();
    }

    [Test]
    public async Task The_contract_names_enums_as_strings()
    {
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(Snapshots.PathOf(_contractPath)));

        var status = contract.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("MatchStatus");

        status.GetProperty("enum").EnumerateArray().Select(v => v.GetString()).Should().Equal("Scheduled", "Live", "FullTime");
    }

    [TestCase("/api/matches/{matchId}", "get")]
    [TestCase("/api/matches/{matchId}/events", "get")]
    [TestCase("/api/matches/{matchId}/moments", "get")]
    [TestCase("/api/matches/{matchId}/momentum", "get")]
    [TestCase("/api/matches/{matchId}/stream", "get")]
    [TestCase("/api/seasons/{season}/players/{playerId}", "get")]
    public async Task Every_read_that_cosmos_db_serves_declares_a_503(string path, string method)
    {
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(Snapshots.PathOf(_contractPath)));

        var responses = contract.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method).GetProperty("responses");

        responses.TryGetProperty("503", out _).Should().BeTrue();
    }

    [Test]
    public async Task The_match_list_documents_its_degraded_header()
    {
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(Snapshots.PathOf(_contractPath)));

        var list = contract.RootElement.GetProperty("paths").GetProperty("/api/matches").GetProperty("get");

        list.GetProperty("description").GetString().Should().Contain(DegradedSources.Header);
        list.GetProperty("responses").GetProperty("200").GetProperty("headers").TryGetProperty(DegradedSources.Header, out _).Should().BeTrue();
        list.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("type").GetString().Should().Be("array");
    }

    // The compiler writes XML doc comments with the build machine's newline, so descriptions
    // carry an escaped \r\n on Windows and \n on the Linux CI runner.
    private static string Normalise(string json) =>
        json.ReplaceLineEndings("\n").Replace("\\r\\n", "\\n", StringComparison.Ordinal).TrimEnd() + "\n";
}
