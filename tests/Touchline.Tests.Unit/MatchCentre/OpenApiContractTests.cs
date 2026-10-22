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

    // The compiler writes XML doc comments with the build machine's newline, so descriptions
    // carry an escaped \r\n on Windows and \n on the Linux CI runner.
    private static string Normalise(string json) =>
        json.ReplaceLineEndings("\n").Replace("\\r\\n", "\\n", StringComparison.Ordinal).TrimEnd() + "\n";
}
