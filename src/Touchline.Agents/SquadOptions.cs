using Microsoft.Extensions.Options;

namespace Touchline.Agents;

/// <summary>
/// The squad's model and budget settings (ADR-007, ADR-043, ADR-045), bound from the
/// <c>Squad</c> configuration section and validated when the host starts.
/// </summary>
public sealed class SquadOptions
{
    /// <summary>The configuration section the options bind from.</summary>
    public const string SectionName = "Squad";

    /// <summary>The languages written ahead of time when none are configured (ADR-045).</summary>
    public static readonly IReadOnlyList<string> DefaultLanguages = ["en", "es", "fr"];

    /// <summary>
    /// The Foundry account's endpoint, for example <c>https://touchline-ai.services.ai.azure.com/</c>.
    /// The clients call its Azure OpenAI v1 API at <c>{endpoint}/openai/v1/</c>.
    /// </summary>
    public Uri? FoundryEndpoint { get; set; }

    /// <summary>
    /// The client ID of the host's user-assigned managed identity, used by the fallback
    /// <c>DefaultAzureCredential</c> to call Foundry. Optional: when it's not set, the credential
    /// reads the <c>AZURE_CLIENT_ID</c> environment variable instead. Each Azure host must set one
    /// of the two to its user-assigned identity's client ID, or the credential can't tell which
    /// identity to use. Leave both unset locally to use the developer's sign-in.
    /// </summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>The model deployment name for each model size.</summary>
    public ModelDeployments Deployments { get; set; } = new();

    /// <summary>The output-token cap for each agent.</summary>
    public AgentTokenCaps MaxOutputTokens { get; set; } = new();

    /// <summary>
    /// The most input tokens a single call may send, estimated. Older conversation turns are
    /// trimmed to fit (see <see cref="TokenBudgetChatClient"/>).
    /// </summary>
    public int MaxInputTokens { get; set; } = 4000;

    /// <summary>
    /// The languages live lines are written in ahead of time (ADR-045). Empty means
    /// <see cref="DefaultLanguages"/>.
    /// </summary>
    public IList<string> Languages { get; set; } = [];
}

/// <summary>The model deployment name for each model size (ADR-007).</summary>
public sealed class ModelDeployments
{
    /// <summary>The small model's deployment, for example <c>gpt-5.4-mini</c>.</summary>
    public string Small { get; set; } = "";

    /// <summary>The large model's deployment, for example <c>gpt-5.5</c>.</summary>
    public string Large { get; set; } = "";

    /// <summary>The deployment name for a model size.</summary>
    /// <param name="size"><see cref="SquadModels.Small"/> or <see cref="SquadModels.Large"/>.</param>
    /// <returns>The deployment name.</returns>
    public string For(string size) => size switch
    {
        SquadModels.Small => Small,
        SquadModels.Large => Large,
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, "Unknown model size.")
    };
}

/// <summary>The output-token cap for each agent (ADR-043).</summary>
public sealed class AgentTokenCaps
{
    /// <summary>The Commentator's cap. Default 150.</summary>
    public int Commentator { get; set; } = 150;

    /// <summary>The Localiser's cap. Default 200.</summary>
    public int Localiser { get; set; } = 200;

    /// <summary>The Analyst's cap. Default 600.</summary>
    public int Analyst { get; set; } = 600;

    /// <summary>The Producer's cap. Default 1,200.</summary>
    public int Producer { get; set; } = 1200;

    /// <summary>The cap for an agent.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>Its output-token cap.</returns>
    public int For(SquadAgent agent) => agent switch
    {
        SquadAgent.Commentator => Commentator,
        SquadAgent.Localiser => Localiser,
        SquadAgent.Analyst => Analyst,
        SquadAgent.Producer => Producer,
        _ => throw new ArgumentOutOfRangeException(nameof(agent), agent, "Unknown agent.")
    };
}

/// <summary>
/// Checks <see cref="SquadOptions"/> when the host starts, so a missing endpoint or deployment
/// fails the deployment instead of the first key moment.
/// </summary>
internal sealed class SquadOptionsValidator : IValidateOptions<SquadOptions>
{
    /// <summary>The smallest input cap that leaves room for a system prompt and a question.</summary>
    internal const int MinimumInputTokens = 256;

    public ValidateOptionsResult Validate(string? name, SquadOptions options)
    {
        var failures = new List<string>();

        if (options.FoundryEndpoint is not { IsAbsoluteUri: true } endpoint || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add("Squad:FoundryEndpoint must be an absolute https URI.");
        }

        if (string.IsNullOrWhiteSpace(options.Deployments.Small))
        {
            failures.Add("Squad:Deployments:Small is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Deployments.Large))
        {
            failures.Add("Squad:Deployments:Large is required.");
        }

        foreach (var agent in Enum.GetValues<SquadAgent>())
        {
            if (options.MaxOutputTokens.For(agent) <= 0)
            {
                failures.Add($"Squad:MaxOutputTokens:{agent} must be greater than zero.");
            }
        }

        if (options.MaxInputTokens < MinimumInputTokens)
        {
            failures.Add($"Squad:MaxInputTokens must be at least {MinimumInputTokens}.");
        }

        if (options.Languages.Count == 0 || options.Languages.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add("Squad:Languages must list at least one language, with no blanks.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
