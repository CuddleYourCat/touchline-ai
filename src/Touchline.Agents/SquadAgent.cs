using Microsoft.Extensions.AI;

namespace Touchline.Agents;

/// <summary>
/// The agents in the squad (ADR-006, ADR-042). Each has its own output-token cap (ADR-043).
/// </summary>
public enum SquadAgent
{
    /// <summary>Writes the live Casual and Analyst lines for a key moment. Small model.</summary>
    Commentator,

    /// <summary>Translates live lines into the languages written ahead of time (ADR-045). Small model.</summary>
    Localiser,

    /// <summary>Answers a fan's "why did that happen?" question. Large model.</summary>
    Analyst,

    /// <summary>Writes the full-time recap. Large model.</summary>
    Producer,

    /// <summary>Writes the search summaries of key-moment possessions at full time (ADR-056). Small model.</summary>
    Indexer
}

/// <summary>
/// The keys of the two keyed <see cref="IChatClient"/> registrations (ADR-007, ADR-043).
/// </summary>
public static class SquadModels
{
    /// <summary>The small, fast model, for the jobs that run many times a match.</summary>
    public const string Small = "small";

    /// <summary>The larger model, for the jobs that run a few times a match.</summary>
    public const string Large = "large";
}

/// <summary>
/// Helpers for naming the agent behind a chat call, which is how
/// <see cref="TokenBudgetChatClient"/> picks the agent's token cap and tags its telemetry.
/// </summary>
public static class SquadAgentChatOptions
{
    /// <summary>
    /// The <see cref="ChatOptions.AdditionalProperties"/> key that carries the agent's name.
    /// The budget client removes it before the request leaves the process.
    /// </summary>
    public const string AgentPropertyKey = "touchline.agent";

    /// <summary>
    /// Returns a copy of <paramref name="options"/> (or new options) that names <paramref name="agent"/>.
    /// The original is never changed.
    /// </summary>
    /// <param name="options">The options to copy, or <see langword="null"/>.</param>
    /// <param name="agent">The agent making the call.</param>
    /// <returns>The copied options, naming the agent.</returns>
    public static ChatOptions ForAgent(this ChatOptions? options, SquadAgent agent)
    {
        var copy = options?.Clone() ?? new ChatOptions();
        copy.AdditionalProperties = copy.AdditionalProperties is null
            ? []
            : new AdditionalPropertiesDictionary(copy.AdditionalProperties);
        copy.AdditionalProperties[AgentPropertyKey] = agent.ToString();
        return copy;
    }

    /// <summary>
    /// Which model an agent runs on (ADR-007): the Commentator and Localiser on the small model,
    /// the Analyst and Producer on the large one.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns><see cref="SquadModels.Small"/> or <see cref="SquadModels.Large"/>.</returns>
    public static string ModelFor(SquadAgent agent) => agent switch
    {
        SquadAgent.Commentator or SquadAgent.Localiser or SquadAgent.Indexer => SquadModels.Small,
        SquadAgent.Analyst or SquadAgent.Producer => SquadModels.Large,
        _ => throw new ArgumentOutOfRangeException(nameof(agent), agent, "Unknown agent.")
    };

    /// <summary>Reads the agent named in <paramref name="options"/>, if any.</summary>
    internal static SquadAgent? ReadAgent(ChatOptions? options)
    {
        if (options?.AdditionalProperties is null
            || !options.AdditionalProperties.TryGetValue(AgentPropertyKey, out object? value))
        {
            return null;
        }

        return value switch
        {
            SquadAgent agent when Enum.IsDefined(agent) => agent,
            string name when Enum.TryParse(name, ignoreCase: true, out SquadAgent parsed)
                && Enum.IsDefined(parsed)
                && !char.IsDigit(name.TrimStart()[0]) => parsed,
            _ => null
        };
    }
}
