namespace Touchline.Agents;

/// <summary>
/// Feature switches the squad checks on every call, not once at start-up (ADR-062). The host
/// supplies an implementation backed by feature flags; with none registered, every switch is on.
/// </summary>
public interface ISquadFeatureGate
{
    /// <summary>Whether the squad may call its models. Off means template lines and fixed answers, with no model call.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>True if model calls are allowed.</returns>
    ValueTask<bool> ModelsEnabledAsync(CancellationToken cancellationToken);

    /// <summary>Whether the Analyst's possession search returns results. Off means it finds nothing.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>True if the search may run.</returns>
    ValueTask<bool> PossessionSearchEnabledAsync(CancellationToken cancellationToken);
}

/// <summary>Combines the squad's options with the optional feature gate.</summary>
public static class SquadFeatureGates
{
    /// <summary>True when models are configured (<see cref="SquadOptions.ModelsAvailable"/>) and the gate, if any, allows them.</summary>
    /// <param name="options">The squad's current options.</param>
    /// <param name="gate">The feature gate, or null for all on.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>True if a model may be called.</returns>
    public static async ValueTask<bool> ModelsAvailableAsync(SquadOptions options, ISquadFeatureGate? gate, CancellationToken cancellationToken) =>
        options.ModelsAvailable && (gate is null || await gate.ModelsEnabledAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>True when there is no gate, or the gate allows possession search.</summary>
    /// <param name="gate">The feature gate, or null for all on.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>True if the search may run.</returns>
    public static async ValueTask<bool> PossessionSearchEnabledAsync(ISquadFeatureGate? gate, CancellationToken cancellationToken) =>
        gate is null || await gate.PossessionSearchEnabledAsync(cancellationToken).ConfigureAwait(false);
}
