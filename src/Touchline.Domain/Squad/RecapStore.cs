using Touchline.Domain.Viewing;

namespace Touchline.Domain.Squad;

/// <summary>
/// Where full-time recaps are kept (ADR-030, amended 23 Oct): the Event Processor writes each
/// match's recaps once, at full time, and the Match Centre only reads them. Implemented over the
/// Cosmos DB <c>recaps</c> container.
/// </summary>
public interface IRecapStore
{
    /// <summary>Reads the recap stored for a match, mode and language: a point read.</summary>
    /// <param name="matchId">The match.</param>
    /// <param name="mode">The viewing mode.</param>
    /// <param name="language">The language tag, matched case-insensitively.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The recap, or null if none is stored.</returns>
    Task<MatchRecap?> GetAsync(string matchId, ViewingMode mode, string language, CancellationToken cancellationToken);

    /// <summary>
    /// Saves a recap if its slot (match, mode and language) is empty. A recap already stored is
    /// kept, so a redelivered message never overwrites one.
    /// </summary>
    /// <param name="recap">The recap.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True if it was stored, false if a recap was already there.</returns>
    Task<bool> SaveAsync(MatchRecap recap, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a pair's attempt marker: how many times the model has fallen back for a match, mode
    /// and language, so re-announcements stop paying for a pair that keeps failing.
    /// </summary>
    /// <param name="matchId">The match.</param>
    /// <param name="mode">The viewing mode.</param>
    /// <param name="language">The language tag, matched case-insensitively.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The count, or 0 if no marker is stored.</returns>
    Task<int> GetAttemptsAsync(string matchId, ViewingMode mode, string language, CancellationToken cancellationToken);

    /// <summary>Creates or replaces a pair's attempt marker.</summary>
    /// <param name="matchId">The match.</param>
    /// <param name="mode">The viewing mode.</param>
    /// <param name="language">The language tag.</param>
    /// <param name="attempts">The new count.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes once the marker is saved.</returns>
    Task SaveAttemptsAsync(string matchId, ViewingMode mode, string language, int attempts, CancellationToken cancellationToken);
}
