using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;

namespace Touchline.Tests.Unit.Architecture;

[TestFixture]
public class DomainDependencyTests
{
    private const string _because = "the domain is pure C# so match logic and skill shots test without any cloud dependency";

    private static readonly string[] _forbiddenPrefixes = ["Azure.", "Microsoft.Azure.", "Microsoft.Extensions.AI"];

    [Test]
    public void Domain_project_declares_no_Azure_or_AI_package()
    {
        var project = XDocument.Load(Path.Combine(RepositoryFiles.Root(), "src", "Touchline.Domain", "Touchline.Domain.csproj"));

        var packages = project.Descendants("PackageReference")
            .Select(reference => (string?)reference.Attribute("Include") ?? string.Empty);

        packages.Where(IsForbidden).Should().BeEmpty(_because);
    }

    [Test]
    public void Domain_project_references_no_other_project()
    {
        var project = XDocument.Load(Path.Combine(RepositoryFiles.Root(), "src", "Touchline.Domain", "Touchline.Domain.csproj"));

        project.Descendants("ProjectReference").Should().BeEmpty(
            "a project reference would bring that project's dependencies into the domain with it");
    }

    [Test]
    public void Domain_assembly_uses_no_Azure_or_AI_assembly()
    {
        var domain = Assembly.Load("Touchline.Domain");

        var referenced = domain.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty);

        referenced.Where(IsForbidden).Should().BeEmpty(_because);
    }

    private static bool IsForbidden(string name) =>
        _forbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
}
