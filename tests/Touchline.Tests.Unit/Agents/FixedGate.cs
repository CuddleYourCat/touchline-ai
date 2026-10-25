using Touchline.Agents;

namespace Touchline.Tests.Unit.Agents;

/// <summary>A feature gate with fixed answers, and a count of how often it was asked.</summary>
internal sealed class FixedGate(bool models = true, bool possessionSearch = true) : ISquadFeatureGate
{
    public int ModelChecks { get; private set; }

    public ValueTask<bool> ModelsEnabledAsync(CancellationToken cancellationToken)
    {
        ModelChecks++;
        return ValueTask.FromResult(models);
    }

    public ValueTask<bool> PossessionSearchEnabledAsync(CancellationToken cancellationToken) => ValueTask.FromResult(possessionSearch);
}
