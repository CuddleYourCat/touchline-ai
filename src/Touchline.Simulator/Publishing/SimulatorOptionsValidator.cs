using Microsoft.Extensions.Options;
using Touchline.Domain.Matches;

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

        // A fan match request is the whole run (ADR-050): the configured matches are ignored, and
        // the request itself is read and checked when the run starts, since it lives in Cosmos DB.
        if (options.PlaysRequest)
        {
            if (!FanMatchIds.IsFanMatch(options.RequestId))
            {
                failures.Add($"Simulator:RequestId '{options.RequestId}' isn't a fan match ID (exh-yyyyMMddHHmm-xxxxxxxx).");
            }

            if (!double.IsFinite(options.RequestSpeed) || options.RequestSpeed <= 0)
            {
                failures.Add("Simulator:RequestSpeed must be positive.");
            }

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }

        // A misplaced or missing section binds to an empty list, which would play nothing and
        // still succeed: as a job, an execution that "worked" having published nothing.
        if (options.Matches.Count == 0)
        {
            failures.Add("Simulator:Matches is empty, so there is nothing to play. Check the section is named Simulator and holds a Matches list.");
        }

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
                _ = plan.CreateSimulator(TimeProvider.System);
            }
            catch (ArgumentException ex)
            {
                failures.Add($"{label}: {ex.Message}");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
