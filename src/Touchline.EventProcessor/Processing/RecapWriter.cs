using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Touchline.Agents;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
using Touchline.Domain.Teams;
using Touchline.Domain.Viewing;

namespace Touchline.EventProcessor.Processing;

/// <summary>
/// Writes a finished match's recaps once, up front (ADR-030, amended 23 Oct): one per language in
/// <see cref="SquadOptions.Languages"/> and viewing mode, by <see cref="ProducerRecap"/>, each saved
/// through <see cref="IRecapStore"/>. The pairs run one after another, because concurrency is low
/// and the large model's quota is the limit.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A recap already stored is skipped, so a redelivered message costs reads, not model calls.</item>
/// <item>A pair whose model call fails, or whose recap fails the guardrail twice, stores nothing:
/// the Match Centre serves the template for it, and its attempt marker counts the fallback. After
/// <see cref="MaxModelAttempts"/> fallbacks the pair isn't tried again. The other pairs carry on.</item>
/// <item>A pair whose model call failed transiently (throttled, a server error or a timeout; ADR-052)
/// stores nothing and isn't counted on its attempt marker. It's counted in
/// <see cref="RecapRun.TransientFailures"/>, so the caller abandons the message and Service Bus
/// redelivers it; the pairs stored meanwhile are skipped on the retry. A guardrail miss or a
/// content-filter block is permanent and counts as a fallback.</item>
/// <item>With no models (<see cref="SquadOptions.ModelsAvailable"/> false), nothing is read or
/// stored, and the Match Centre serves the template recap.</item>
/// </list>
/// </remarks>
public sealed partial class RecapWriter
{
    /// <summary>
    /// How many model fallbacks a pair gets before re-announcements stop trying it, counted by its
    /// attempt marker. Store failures don't count: they're redelivered and retried.
    /// </summary>
    /// <remarks>
    /// The worst case for one run is 12 large-model calls: 6 pairs, each with a guardrail retry.
    /// </remarks>
    public const int MaxModelAttempts = 2;

    private readonly IMatchStore _matches;
    private readonly IRecapStore _recaps;
    private readonly KeyMomentDetector _detector;
    private readonly IChatClient _chatClient;
    private readonly IOptions<SquadOptions> _options;
    private readonly SquadMetrics _metrics;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<RecapWriter> _logger;
    private readonly IMatchTeamProfiles? _teams;
    private readonly ISquadFeatureGate? _gate;

    /// <summary>Creates the writer.</summary>
    /// <param name="matches">The stored matches and events, which the facts are built from.</param>
    /// <param name="recaps">Where the recaps are saved.</param>
    /// <param name="detector">Finds the key moments again from the stored events.</param>
    /// <param name="chatClient">The keyed <see cref="SquadModels.Large"/> client (ADR-043).</param>
    /// <param name="options">The squad's settings: the languages, and whether models are available.</param>
    /// <param name="metrics">Where recaps and fallbacks are counted.</param>
    /// <param name="loggers">Creates the Producer's logger.</param>
    /// <param name="teams">The team profiles of each match (ADR-050); null for the embedded catalogue alone.</param>
    /// <param name="gate">The feature switches, checked for every match, or null for all on (ADR-062).</param>
    public RecapWriter(
        IMatchStore matches,
        IRecapStore recaps,
        KeyMomentDetector detector,
        [FromKeyedServices(SquadModels.Large)] IChatClient chatClient,
        IOptions<SquadOptions> options,
        SquadMetrics metrics,
        ILoggerFactory loggers,
        IMatchTeamProfiles? teams = null,
        ISquadFeatureGate? gate = null)
    {
        _teams = teams;
        _gate = gate;
        ArgumentNullException.ThrowIfNull(loggers);
        _matches = matches ?? throw new ArgumentNullException(nameof(matches));
        _recaps = recaps ?? throw new ArgumentNullException(nameof(recaps));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _loggers = loggers;
        _logger = loggers.CreateLogger<RecapWriter>();
    }

    /// <summary>Writes and saves every recap of a finished match that isn't stored yet.</summary>
    /// <param name="matchId">The match.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>What happened to each pair.</returns>
    public async Task<RecapRun> WriteAsync(string matchId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matchId);
        var options = _options.Value;
        if (!await SquadFeatureGates.ModelsAvailableAsync(options, _gate, cancellationToken))
        {
            LogNoModels(_logger, matchId);
            return new RecapRun(RecapRunStatus.NoModels, 0, 0, 0, 0);
        }

        var match = await _matches.GetMatchAsync(matchId, cancellationToken);
        if (match is not { State.FullTime: true })
        {
            return new RecapRun(RecapRunStatus.NotFinished, 0, 0, 0, 0);
        }

        var events = await _matches.GetEventsAsync(matchId, cancellationToken);
        var profiles = _teams is null ? null : await _teams.ForMatchAsync(matchId, cancellationToken);
        var detector = profiles is null ? _detector : _detector.ForTeams(profiles);
        var producer = new ProducerRecap(
            _chatClient, StoredMatchFacts.From(match, events, detector, profiles), _options, _metrics, _loggers.CreateLogger<ProducerRecap>(), _gate);

        int written = 0, skipped = 0, fallbacks = 0, storeFailures = 0, givenUp = 0, transient = 0;
        foreach (var language in options.Languages.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var mode in Enum.GetValues<ViewingMode>())
            {
                var (viewer, _) = Viewer.Create(mode, language, null, null, null, _ => true, _ => true);
                if (viewer is null)
                {
                    continue;
                }

                try
                {
                    var attempts = await _recaps.GetAttemptsAsync(matchId, mode, language, cancellationToken);
                    if (attempts >= MaxModelAttempts)
                    {
                        givenUp++;
                        continue;
                    }

                    // The stored check is the last read before the model call, so a recap saved by
                    // an overlapping delivery (a lock that lapsed mid-run) is seen and not paid for twice.
                    if (await _recaps.GetAsync(matchId, mode, language, cancellationToken) is not null)
                    {
                        skipped++;
                        _metrics.RecordRecap("skipped");
                        continue;
                    }

                    var produced = await producer.WriteAsync(matchId, viewer, cancellationToken);
                    if (produced.TransientFailure)
                    {
                        // Not an attempt: the model never answered, so the redelivery gets a fair try.
                        transient++;
                        continue;
                    }

                    if (!produced.FromModel)
                    {
                        fallbacks++;
                        await RecordAttemptAsync(matchId, mode, language, attempts + 1, cancellationToken);
                        continue;
                    }

                    await _recaps.SaveAsync(produced.Recap, cancellationToken);
                    written++;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    storeFailures++;
                    LogPairFailed(_logger, ex, matchId, mode, language);
                }
            }
        }

        LogWritten(_logger, matchId, written, skipped, fallbacks, storeFailures, givenUp, transient);
        return new RecapRun(RecapRunStatus.Done, written, skipped, fallbacks, storeFailures, givenUp, transient);
    }

    // Best effort: a marker that fails to save only means one more paid attempt later, so it
    // isn't a store failure and doesn't make the message redeliver.
    private async Task RecordAttemptAsync(string matchId, ViewingMode mode, string language, int attempts, CancellationToken cancellationToken)
    {
        try
        {
            await _recaps.SaveAttemptsAsync(matchId, mode, language, attempts, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogAttemptNotSaved(_logger, ex, matchId, mode, language);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The attempt marker for the {Mode} recap in {Language} for match {MatchId} couldn't be saved")]
    private static partial void LogAttemptNotSaved(ILogger logger, Exception exception, string matchId, ViewingMode mode, string language);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Squad:FoundryEndpoint is not set, so no recaps are written for match {MatchId}; the Match Centre serves the template")]
    private static partial void LogNoModels(ILogger logger, string matchId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Mode} recap in {Language} for match {MatchId} couldn't be read or saved")]
    private static partial void LogPairFailed(ILogger logger, Exception exception, string matchId, ViewingMode mode, string language);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recaps for match {MatchId}: {Written} written, {Skipped} already stored, {Fallbacks} left to the template, {Failed} failed, {GivenUp} given up after repeated fallbacks, {Transient} to retry after a transient model failure")]
    private static partial void LogWritten(ILogger logger, string matchId, int written, int skipped, int fallbacks, int failed, int givenUp, int transient);
}

/// <summary>How a recap run ended.</summary>
public enum RecapRunStatus
{
    /// <summary>Every pair was tried.</summary>
    Done,

    /// <summary>The match isn't stored, or isn't at full time.</summary>
    NotFinished,

    /// <summary>No models are configured, so nothing was written.</summary>
    NoModels
}

/// <summary>What one recap run did.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Written">Recaps the model wrote and the store saved.</param>
/// <param name="Skipped">Recaps already stored.</param>
/// <param name="Fallbacks">Pairs left to the template: the model failed, or the guardrail caught it twice.</param>
/// <param name="StoreFailures">Pairs whose read or save failed, which a redelivery retries.</param>
/// <param name="GivenUp">Pairs not tried, because the model already fell back <see cref="RecapWriter.MaxModelAttempts"/> times.</param>
/// <param name="TransientFailures">Pairs whose model call failed transiently (ADR-052), which a redelivery retries.</param>
public sealed record RecapRun(RecapRunStatus Status, int Written, int Skipped, int Fallbacks, int StoreFailures, int GivenUp = 0, int TransientFailures = 0);
