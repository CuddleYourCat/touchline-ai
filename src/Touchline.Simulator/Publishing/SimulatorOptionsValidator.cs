using Microsoft.Extensions.Options;

namespace Touchline.Simulator.Publishing;

/// <summary>
/// Checks every configured match when the worker starts, so a mistake (an unknown team or
/// referee, a fixture without a season, a speed of zero) stops it before anything is
/// published, rather than part-way through the list.
/// </summary>
public sealed class SimulatorOptionsValidator : IValidateOptions<SimulatorOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, SimulatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        foreach (var (plan, index) in options.Matches.Select((p, i) => (p, i)))
        {
            var label = $"Simulator:Matches:{index} (seed {plan.Seed})";
            if (!double.IsFinite(plan.Speed) || plan.Speed <= 0)
            {
                failures.Add($"{label} needs a positive Speed.");
            }

            try
            {
                // Building the simulator checks the teams, the referee and the kind of match,
                // without playing anything.
                _ = plan.CreateSimulator();
            }
            catch (ArgumentException ex)
            {
                failures.Add($"{label}: {ex.Message}");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
