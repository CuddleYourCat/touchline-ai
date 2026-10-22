using System.Globalization;
using System.Security;
using Touchline.Domain.Teams.Builder;
using Touchline.MatchCentre.Api;

namespace Touchline.MatchCentre.TeamBuilding;

/// <summary>
/// Draws a crest as SVG from its spec (ADR-031). Every input is validated (shape and emblem
/// from fixed sets, initials A to Z, colours as hex), and the output is plain shapes with no
/// script, links or external references.
/// </summary>
internal static class CrestSvg
{
    /// <summary>The SVG media type.</summary>
    public const string ContentType = "image/svg+xml";

    /// <summary>Renders a crest on a 100 × 120 canvas.</summary>
    /// <param name="crest">The crest.</param>
    /// <returns>The SVG document.</returns>
    public static string Render(CrestSpec crest)
    {
        ArgumentNullException.ThrowIfNull(crest);
        var primary = SecurityElement.Escape(crest.Primary);
        var secondary = SecurityElement.Escape(crest.Secondary);
        var initials = SecurityElement.Escape(crest.Initials);
        var emblem = Emblem(crest.Emblem);
        var initialsY = emblem.Length == 0 ? 72 : 98;

        return string.Create(CultureInfo.InvariantCulture, $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 120" width="100" height="120" role="img" aria-label="Crest">
              <path d="{Outline(crest.Shape)}" fill="{primary}" stroke="{secondary}" stroke-width="5" stroke-linejoin="round"/>
              <g fill="{secondary}" color="{secondary}" transform="translate(50 52)">{emblem}</g>
              <text x="50" y="{initialsY}" text-anchor="middle" font-family="Arial, Helvetica, sans-serif" font-weight="700" font-size="18" fill="{secondary}">{initials}</text>
            </svg>
            """);
    }

    private static string Outline(CrestShape shape) => shape switch
    {
        CrestShape.Shield => "M8 8 H92 V60 C92 88 70 106 50 114 C30 106 8 88 8 60 Z",
        CrestShape.Round => "M50 6 A52 52 0 1 1 49.9 6 Z",
        CrestShape.Diamond => "M50 4 L96 60 L50 116 L4 60 Z",
        CrestShape.Banner => "M10 6 H90 V112 L50 92 L10 112 Z",
        CrestShape.Hexagon => "M50 4 L94 30 V90 L50 116 L6 90 V30 Z",
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown crest shape.")
    };

    // Each emblem is drawn around (0, 0), about 40 units across.
    private static string Emblem(CrestEmblem emblem) => emblem switch
    {
        CrestEmblem.None => "",
        CrestEmblem.Star => """<path d="M0 -20 L6 -6 L21 -6 L9 3 L13 18 L0 9 L-13 18 L-9 3 L-21 -6 L-6 -6 Z"/>""",
        CrestEmblem.Ball => """<circle r="18" fill="none" stroke="currentColor" stroke-width="4"/><path d="M0 -8 L8 -2 L5 8 L-5 8 L-8 -2 Z"/>""",
        CrestEmblem.Oak => """<circle cy="-6" r="14"/><rect x="-3" y="4" width="6" height="16"/>""",
        CrestEmblem.Anchor => """<circle cy="-15" r="4"/><rect x="-2" y="-12" width="4" height="30"/><path d="M-16 8 Q0 26 16 8 L12 8 Q0 20 -12 8 Z"/>""",
        CrestEmblem.Crown => """<path d="M-20 12 L-20 -10 L-10 2 L0 -14 L10 2 L20 -10 L20 12 Z"/>""",
        CrestEmblem.Bolt => """<path d="M4 -20 L-12 4 L-1 4 L-6 20 L12 -4 L1 -4 Z"/>""",
        CrestEmblem.Wave => """<path d="M-20 0 Q-10 -12 0 0 T20 0 L20 8 Q10 -4 0 8 T-20 8 Z"/>""",
        CrestEmblem.Tower => """<path d="M-10 20 V-8 H-14 V-18 H-8 V-13 H-3 V-18 H3 V-13 H8 V-18 H14 V-8 H10 V20 Z"/>""",
        CrestEmblem.Wing => """<path d="M-18 10 Q-18 -16 18 -18 Q6 -10 10 -6 Q0 -4 4 2 Q-6 2 -2 8 Q-10 8 -18 10 Z"/>""",
        CrestEmblem.Key => """<circle cx="-8" r="8" fill="none" stroke="currentColor" stroke-width="4"/><rect x="0" y="-2" width="20" height="4"/><rect x="14" y="2" width="4" height="6"/>""",
        CrestEmblem.Bridge => """<path d="M-20 10 V2 Q0 -18 20 2 V10 H14 V6 Q0 -8 -14 6 V10 Z"/>""",
        _ => throw new ArgumentOutOfRangeException(nameof(emblem), emblem, "Unknown emblem.")
    };
}
