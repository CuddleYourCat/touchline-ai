using System.ComponentModel.DataAnnotations;

namespace Touchline.MatchCentre.Maintenance;

/// <summary>
/// Maintenance mode (ADR-060): while it's on, the Match Centre answers every request except the
/// health probes with a 503. Bound from <c>MatchCentre:Maintenance</c>; in Azure,
/// <c>deploy.ps1 -Action wipe-data</c> turns it on with the <c>MatchCentre__Maintenance__Enabled</c>
/// setting before it deletes anything, and off when it ends.
/// </summary>
public sealed class MaintenanceOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "MatchCentre:Maintenance";

    /// <summary>Whether maintenance mode is on. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>The <c>Retry-After</c> value, in seconds, and how often the page reloads itself.</summary>
    [Range(5, 3600)]
    public int RetryAfterSeconds { get; set; } = 30;

    /// <summary>Roughly how many minutes until the Match Centre is back, shown on the page.</summary>
    [Range(1, 240)]
    public int EstimatedMinutes { get; set; } = 15;
}
