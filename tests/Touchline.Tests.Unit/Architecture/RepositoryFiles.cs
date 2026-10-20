namespace Touchline.Tests.Unit.Architecture;

/// <summary>Finds the repository's own files, for tests that check its source and project files.</summary>
internal static class RepositoryFiles
{
    /// <summary>The folder that holds <c>Touchline.slnx</c>, found by walking up from the test output.</summary>
    public static string Root()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Touchline.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Touchline.slnx was not found above the test output directory.");
    }
}
