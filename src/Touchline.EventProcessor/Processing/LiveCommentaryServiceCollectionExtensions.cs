using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Touchline.Agents;
using Touchline.Domain.Squad;
using Touchline.Storage;

namespace Touchline.EventProcessor.Processing;

/// <summary>Registers the live commentary (ADR-030): the store, the workflow and the squad's model clients.</summary>
public static class LiveCommentaryServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="CosmosCommentaryStore"/> as <see cref="ICommentaryStore"/>,
    /// <see cref="CommentaryWorkflow"/>, <see cref="SquadMetrics"/> and the two keyed model
    /// clients. With <c>Squad:FoundryEndpoint</c> set, the clients are the real ones
    /// (<see cref="SquadModelServiceCollectionExtensions.AddSquadModels"/>, validated on start).
    /// Without it (local Aspire with no models) both keys get <see cref="UnavailableChatClient"/>,
    /// the options aren't validated, <see cref="SquadOptions.ModelsAvailable"/> is false, a warning
    /// is logged once at start-up, and every moment
    /// stores its English template lines.
    /// </summary>
    /// <param name="services">The service collection. A <see cref="Microsoft.Azure.Cosmos.CosmosClient"/> must also be registered.</param>
    /// <param name="configuration">The configuration holding the optional <c>Squad</c> section.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddLiveCommentary(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<ICommentaryStore, CosmosCommentaryStore>();
        services.TryAddSingleton<CommentaryWorkflow>();

        if (IsFoundryConfigured(configuration))
        {
            return services.AddSquadModels(configuration);
        }

        // No validation here: the validator would reject the missing endpoint and stop the
        // host, and the point is that the processor still runs without models.
        services.AddOptions<SquadOptions>()
            .Bind(configuration.GetSection(SquadOptions.SectionName))
            .PostConfigure(options =>
            {
                if (options.Languages.Count == 0)
                {
                    options.Languages = [.. SquadOptions.DefaultLanguages];
                }

                options.ModelsAvailable = false;
            });
        services.AddMetrics();
        services.TryAddSingleton<SquadMetrics>();
        services.AddKeyedSingleton<IChatClient>(SquadModels.Small, (_, _) => new UnavailableChatClient());
        services.AddKeyedSingleton<IChatClient>(SquadModels.Large, (_, _) => new UnavailableChatClient());
        services.AddHostedService<ModelsUnavailableNotice>();
        return services;
    }

    /// <summary>Whether <c>Squad:FoundryEndpoint</c> has a value.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>True if the real model clients should be registered.</returns>
    public static bool IsFoundryConfigured(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !string.IsNullOrWhiteSpace(configuration[$"{SquadOptions.SectionName}:{nameof(SquadOptions.FoundryEndpoint)}"]);
    }
}

/// <summary>
/// The stand-in model client when no Foundry endpoint is configured: every call throws, so the
/// commentary workflow falls back to its template lines (ADR-044).
/// </summary>
public sealed class UnavailableChatClient : IChatClient
{
    /// <summary>The message every call throws with.</summary>
    public const string Reason = "No model is configured: Squad:FoundryEndpoint is not set.";

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Reason);

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Reason);

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType?.IsInstanceOfType(this) == true ? this : null;

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>Logs, once at start-up, that the squad has no models and commentary will be template lines.</summary>
/// <param name="logger">The logger.</param>
internal sealed partial class ModelsUnavailableNotice(ILogger<ModelsUnavailableNotice> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogNoModels(logger);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Squad:FoundryEndpoint is not set, so the squad has no models: live commentary will store template lines only")]
    private static partial void LogNoModels(ILogger logger);
}
