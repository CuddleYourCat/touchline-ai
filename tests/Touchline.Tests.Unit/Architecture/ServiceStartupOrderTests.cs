using FluentAssertions;

namespace Touchline.Tests.Unit.Architecture;

/// <summary>
/// The Azure SDK spans depend on the service defaults running first (ADR-024): they set the experimental
/// switch, and Aspire's Service Bus and Event Hubs integrations only add their sources if it's already on.
/// </summary>
[TestFixture]
public class ServiceStartupOrderTests
{
    [Test]
    public void Every_service_adds_the_service_defaults_before_any_Azure_client()
    {
        var programs = Directory.GetDirectories(Path.Combine(RepositoryFiles.Root(), "src"))
            .Select(project => Path.Combine(project, "Program.cs"))
            .Where(File.Exists)
            .Select(path => (Path: path, Source: File.ReadAllText(path)))
            .Where(program => program.Source.Contains(".AddAzure", StringComparison.Ordinal))
            .ToList();

        programs.Should().NotBeEmpty("the simulator and the processor both register Azure clients");
        programs.Should().OnlyContain(
            program => IndexOf(program.Source, ".AddServiceDefaults(") < IndexOf(program.Source, ".AddAzure"),
            "a client registered before the service defaults would be untraced, with nothing to show it");
    }

    private static int IndexOf(string source, string call)
    {
        var index = source.IndexOf(call, StringComparison.Ordinal);
        return index < 0 ? int.MaxValue : index;
    }
}
