using Azure.Core;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Touchline.MatchCentre.Caching;

/// <summary>
/// The one Redis connection the Match Centre shares (ADR-061). Every user of it treats a missing
/// database as a cache miss, so a Redis outage never fails a request.
/// </summary>
internal interface IRedisConnection
{
    /// <summary>
    /// The database when Redis is connected, or null when it isn't (yet). Never waits: a first
    /// call starts connecting in the background.
    /// </summary>
    IDatabase? Database { get; }
}

/// <summary>
/// Which failures mean "Redis didn't answer", as opposed to a bug.
/// </summary>
internal static class RedisFailures
{
    /// <summary>Whether a failure is Redis being unreachable, slow or refusing a command.</summary>
    /// <param name="exception">The failure.</param>
    /// <returns>True for a connection, timeout or server error.</returns>
    public static bool IsRedisFailure(Exception exception) =>
        exception is RedisException or TimeoutException or RedisUnavailableException;
}

/// <summary>Redis isn't connected, so a call that needs it can't be made.</summary>
/// <param name="message">What couldn't be done.</param>
internal sealed class RedisUnavailableException(string message) : Exception(message);

/// <summary>
/// Connects lazily and in the background, with short timeouts and fail-fast commands, so a
/// request never waits on a Redis that is down. A failed attempt (for example no Entra ID token)
/// is logged and tried again after <see cref="RedisOptions.RetryConnectAfter"/>. Once connected,
/// StackExchange.Redis reconnects by itself, and <see cref="Database"/> is null while it does.
/// </summary>
internal sealed partial class RedisConnection : IRedisConnection, IAsyncDisposable
{
    private readonly Func<Task<IConnectionMultiplexer>> _connect;
    private readonly TimeSpan _retryAfter;
    private readonly TimeProvider _time;
    private readonly ILogger<RedisConnection> _logger;
    private readonly Lock _gate = new();
    private IConnectionMultiplexer? _multiplexer;
    private Task? _connecting;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;

    /// <summary>Connects with the configured connection string, or the endpoint and the app's Entra ID identity.</summary>
    /// <param name="configuration">The app's configuration, for the Aspire connection string.</param>
    /// <param name="options">The Redis settings.</param>
    /// <param name="credential">The app's credential, for Azure Managed Redis.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Logs connection failures.</param>
    public RedisConnection(
        IConfiguration configuration,
        IOptions<RedisOptions> options,
        TokenCredential credential,
        TimeProvider time,
        ILogger<RedisConnection> logger)
        : this(() => ConnectAsync(configuration, options.Value, credential), options.Value.RetryConnectAfter, time, logger)
    {
    }

    /// <summary>Connects with the given delegate. For tests.</summary>
    /// <param name="connect">Makes a multiplexer.</param>
    /// <param name="retryAfter">How long to wait after a failed attempt.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Logs connection failures.</param>
    internal RedisConnection(Func<Task<IConnectionMultiplexer>> connect, TimeSpan retryAfter, TimeProvider time, ILogger<RedisConnection> logger)
    {
        _connect = connect;
        _retryAfter = retryAfter;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public IDatabase? Database
    {
        get
        {
            var multiplexer = Volatile.Read(ref _multiplexer);
            if (multiplexer is null)
            {
                StartConnecting();
                return null;
            }

            return multiplexer.IsConnected ? multiplexer.GetDatabase() : null;
        }
    }

    /// <summary>The background connection attempt, if one is running. For tests.</summary>
    internal Task Connecting => _connecting ?? Task.CompletedTask;

    /// <summary>The options a connection uses: short timeouts, no waiting for a first connect, and commands that fail at once while disconnected.</summary>
    /// <param name="configuration">The parsed connection string or endpoint.</param>
    /// <param name="options">The Redis settings.</param>
    /// <returns>The same options, for chaining.</returns>
    internal static ConfigurationOptions Harden(ConfigurationOptions configuration, RedisOptions options)
    {
        configuration.AbortOnConnectFail = false;
        configuration.ConnectRetry = 1;
        configuration.ConnectTimeout = (int)options.ConnectTimeout.TotalMilliseconds;
        configuration.SyncTimeout = (int)options.OperationTimeout.TotalMilliseconds;
        configuration.AsyncTimeout = (int)options.OperationTimeout.TotalMilliseconds;
        configuration.BacklogPolicy = BacklogPolicy.FailFast;
        return configuration;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _multiplexer) is { } multiplexer)
        {
            await multiplexer.DisposeAsync();
        }
    }

    private static async Task<IConnectionMultiplexer> ConnectAsync(IConfiguration configuration, RedisOptions options, TokenCredential credential)
    {
        ConfigurationOptions settings;
        if (configuration.GetConnectionString(RedisOptions.ConnectionName) is { Length: > 0 } connectionString)
        {
            // The local Aspire container: its connection string carries the password and any TLS setting.
            settings = Harden(ConfigurationOptions.Parse(connectionString), options);
        }
        else
        {
            // Azure Managed Redis: TLS, access keys off, the app's identity through an access policy assignment.
            settings = Harden(ConfigurationOptions.Parse(options.Endpoint!), options);
            settings.Ssl = true;
            await settings.ConfigureForAzureWithTokenCredentialAsync(credential);
        }

        return await ConnectionMultiplexer.ConnectAsync(settings);
    }

    private void StartConnecting()
    {
        lock (_gate)
        {
            if (_multiplexer is not null || _connecting is { IsCompleted: false } || _time.GetUtcNow() < _nextAttempt)
            {
                return;
            }

            _connecting = Task.Run(TryConnectAsync);
        }
    }

    private async Task TryConnectAsync()
    {
        try
        {
            var multiplexer = await _connect();
            Volatile.Write(ref _multiplexer, multiplexer);
            if (!multiplexer.IsConnected)
            {
                LogNotYetConnected(_logger);
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _nextAttempt = _time.GetUtcNow() + _retryAfter;
            }

            LogConnectFailed(_logger, exception, _retryAfter);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't connect to Redis; the caches stay in memory and the limits in Cosmos DB. Trying again in {RetryAfter}")]
    private static partial void LogConnectFailed(ILogger logger, Exception exception, TimeSpan retryAfter);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis isn't reachable yet; it keeps trying in the background, and every Redis read is a miss until then")]
    private static partial void LogNotYetConnected(ILogger logger);
}

/// <summary>Starts the Redis connection when the app starts, so the first request doesn't find it cold.</summary>
/// <param name="connection">The connection.</param>
internal sealed class RedisConnectionStarter(IRedisConnection connection) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = connection.Database;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
