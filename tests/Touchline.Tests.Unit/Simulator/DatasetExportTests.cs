extern alias simulator;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using FluentAssertions.Execution;
using simulator::Touchline.Simulator.Calibration;
using simulator::Touchline.Simulator.Export;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Teams;

namespace Touchline.Tests.Unit.Simulator;

/// <summary>
/// The synthetic dataset export (ADR-063): deterministic bytes, every exported line valid against
/// <c>schema.json</c>, and the season's averages within the calibration ranges. The season is played
/// once for the fixture; the determinism test plays its own matches again from scratch.
/// </summary>
[TestFixture]
public class DatasetExportTests
{
    private IReadOnlyList<SeasonMatch> _season = null!;
    private JsonElement _schema;

    [OneTimeSetUp]
    public void PlayTheSeason()
    {
        _season = SeasonDataset.Play(TeamProfileCatalog.LoadEmbedded());
        using var document = JsonDocument.Parse(DatasetSchema.Json);
        _schema = document.RootElement.Clone();
    }

    [Test]
    public void Exporting_two_matches_twice_gives_identical_bytes()
    {
        var teams = TeamProfileCatalog.LoadEmbedded();

        var firstRun = new[] { SeasonDataset.Play(teams, 1), SeasonDataset.Play(teams, 2) };
        var secondRun = new[] { SeasonDataset.Play(teams, 1), SeasonDataset.Play(teams, 2) };

        DatasetWriter.EventsGzip(firstRun).Should().Equal(DatasetWriter.EventsGzip(secondRun), "the events file");
        DatasetWriter.MatchesJsonl(firstRun).Should().Equal(DatasetWriter.MatchesJsonl(secondRun), "the matches file");
    }

    [Test]
    public void The_gzip_header_carries_no_time_and_no_platform()
    {
        var bytes = DatasetWriter.EventsGzip(_season.Take(1).ToList());

        bytes[4..8].Should().Equal(new byte[] { 0, 0, 0, 0 }, "the modification time is zero");
        bytes[9].Should().Be(255, "the OS byte is fixed, so Windows and Linux write the same header");
    }

    [Test]
    public void Every_exported_event_line_validates_against_the_schema()
    {
        var bytes = DatasetWriter.EventsGzip(_season);
        using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        var eventSchema = _schema.GetProperty("$defs").GetProperty("event");
        var lines = 0;
        var problems = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            problems.AddRange(SchemaSubset.Problems(document.RootElement, eventSchema, _schema).Select(p => $"line {lines + 1}: {p}"));
            lines++;
        }

        using (new AssertionScope())
        {
            lines.Should().Be(_season.Sum(m => m.Events.Count), "one line per event");
            problems.Take(20).Should().BeEmpty("every event line should match schema.json");
        }
    }

    [Test]
    public void Every_exported_match_line_validates_against_the_schema()
    {
        var matchSchema = _schema.GetProperty("$defs").GetProperty("match");
        var lines = Encoding.UTF8.GetString(DatasetWriter.MatchesJsonl(_season))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var problems = new List<string>();
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            problems.AddRange(SchemaSubset.Problems(document.RootElement, matchSchema, _schema));
        }

        using (new AssertionScope())
        {
            lines.Should().HaveCount(SeasonDataset.Seeds.Count);
            problems.Take(20).Should().BeEmpty("every match line should match schema.json");
        }
    }

    [Test]
    public void The_schema_rejects_a_missing_or_unknown_property()
    {
        var eventSchema = _schema.GetProperty("$defs").GetProperty("event");
        var node = JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(_season[0].Events[1], DomainJson.Options))!.AsObject();
        node.Remove("shot");
        node["unexpected"] = 1;

        using var document = JsonDocument.Parse(node.ToJsonString());
        var problems = SchemaSubset.Problems(document.RootElement, eventSchema, _schema);

        problems.Should().Contain(p => p.Contains("missing required 'shot'", StringComparison.Ordinal));
        problems.Should().Contain(p => p.Contains("unexpected 'unexpected'", StringComparison.Ordinal));
    }

    [Test]
    public void The_season_is_38_head_to_head_matches_with_home_and_away_alternating()
    {
        using (new AssertionScope())
        {
            _season.Should().HaveCount(38);
            for (var i = 0; i < _season.Count; i++)
            {
                var match = _season[i];
                var seed = i + 1;
                match.Seed.Should().Be(seed);
                match.Metadata.Round.Should().Be(seed);
                match.Metadata.Season.Should().Be(SeasonDataset.Season);
                match.Metadata.Kind.Should().Be(MatchKind.Fixture);
                match.Metadata.Home.Id.Should().Be(seed % 2 == 1 ? SeasonDataset.FirstClubId : SeasonDataset.SecondClubId);
                match.Metadata.Away.Id.Should().Be(seed % 2 == 1 ? SeasonDataset.SecondClubId : SeasonDataset.FirstClubId);
            }
        }
    }

    [Test]
    public void The_season_averages_land_in_the_calibration_ranges()
    {
        var results = CalibrationRanges.Evaluate(_season.Select(m => (IReadOnlyList<MatchEvent>)m.Events).ToList());

        // Printed so the report's numbers are visible in the test output, even when they pass.
        foreach (var result in results)
        {
            TestContext.Out.WriteLine(
                $"{result.Label}: {result.Value:0.000} (accepted {result.Accepted}, standard error {result.StandardError:0.000}) inside={result.InRange} withinSampling={result.WithinSampling}");
        }

        using var scope = new AssertionScope();
        foreach (var result in results)
        {
            result.WithinSampling.Should().BeTrue($"{result.Label} is {result.Value:0.000}, accepted {result.Accepted}, standard error {result.StandardError:0.000}");
        }
    }

    [Test]
    public void The_committed_dataset_matches_a_fresh_export()
    {
        var folder = Path.Combine(RepositoryRoot(), DatasetWriter.DefaultFolder);
        var results = CalibrationRanges.Evaluate(_season.Select(m => (IReadOnlyList<MatchEvent>)m.Events).ToList());

        // The events are compared decompressed: the gzip bytes depend on the runtime's zlib, which
        // can differ between Windows and Linux CI, but the events themselves can't.
        var drifted = new List<string>();
        var committedEvents = Gunzip(File.ReadAllBytes(Path.Combine(folder, "events.jsonl.gz")));
        if (!committedEvents.SequenceEqual(Gunzip(DatasetWriter.EventsGzip(_season))))
        {
            drifted.Add("events.jsonl.gz");
        }

        AddIfDrifted(drifted, folder, "matches.jsonl", DatasetWriter.MatchesJsonl(_season));
        AddIfDrifted(drifted, folder, "schema.json", Encoding.UTF8.GetBytes(DatasetWriter.Schema));
        AddIfDrifted(drifted, folder, "CALIBRATION.md", Encoding.UTF8.GetBytes(DatasetWriter.CalibrationReport(results, _season.Count)));
        AddIfDrifted(drifted, folder, "README.md", Encoding.UTF8.GetBytes(DatasetWriter.Readme));

        drifted.Should().BeEmpty(
            $"the committed dataset/ no longer matches a fresh export (differs: {string.Join(", ", drifted)}). " +
            "Regenerate it with: dotnet run --project src/Touchline.Simulator -- export --out dataset");
    }

    [Test]
    public async Task Export_with_out_and_no_folder_says_the_folder_is_missing()
    {
        Func<Task> export = () => DatasetWriter.RunAsync(["--out"], TextWriter.Null, CancellationToken.None);

        await export.Should().ThrowAsync<ArgumentException>().WithMessage("*needs a folder*");
    }

    private static void AddIfDrifted(List<string> drifted, string folder, string name, byte[] fresh)
    {
        var path = Path.Combine(folder, name);
        if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(fresh))
        {
            drifted.Add(name);
        }
    }

    private static byte[] Gunzip(byte[] gzipped)
    {
        using var gzip = new GZipStream(new MemoryStream(gzipped), CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    // The folder holding Touchline.slnx, found the same way the writer finds it.
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Touchline.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (the folder with Touchline.slnx) wasn't found above the test output.");
    }
}
