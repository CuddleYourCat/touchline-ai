using Touchline.Tests.Unit.Architecture;

namespace Touchline.Tests.Unit.MatchCentre;

/// <summary>
/// Committed files that are generated from the code (the demo recording, the OpenAPI document).
/// Their tests fail when the code moves on without them. To regenerate them, run the tests
/// with TOUCHLINE_UPDATE_SNAPSHOTS=1, rebuild, and commit the result.
/// </summary>
internal static class Snapshots
{
    public const string UpdateVariable = "TOUCHLINE_UPDATE_SNAPSHOTS";

    public static bool Updating => Environment.GetEnvironmentVariable(UpdateVariable) == "1";

    public static string PathOf(params string[] parts) => Path.Combine([RepositoryFiles.Root(), .. parts]);

    public static string StaleMessage(string what) =>
        $"{what} is out of date. Run the tests with {UpdateVariable}=1, rebuild, and commit the regenerated file.";
}
