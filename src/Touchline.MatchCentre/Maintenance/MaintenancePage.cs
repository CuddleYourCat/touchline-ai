using System.Globalization;

namespace Touchline.MatchCentre.Maintenance;

/// <summary>
/// The maintenance page (ADR-060). Self-contained, with inline styles, an inline SVG and no scripts
/// or bundles, because the app's hashed bundles aren't served while maintenance is on. The colours
/// are the Match Centre's theme tokens, light and dark.
/// </summary>
public static class MaintenancePage
{
    // {0} is the reload interval in seconds, {1} the estimated minutes until the app is back.
    private const string _template = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta http-equiv="refresh" content="{0}">
        <title>Touchline: pitch maintenance</title>
        <style>
        :root {{ --bg: #0a1020; --surface: #111a2e; --line: #22304d; --text: #e7edf8; --muted: #8b9bbd; --accent: #ff8a1f; --highlight: #39d3df; }}
        @media (prefers-color-scheme: light) {{
          :root {{ --bg: #f3f6fb; --surface: #ffffff; --line: #d5ddeb; --text: #0e1830; --muted: #52617f; --accent: #c25e00; --highlight: #0b7f8a; }}
        }}
        * {{ box-sizing: border-box; }}
        body {{ margin: 0; min-height: 100vh; display: flex; flex-direction: column; background: var(--bg); color: var(--text);
          font: 16px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }}
        header {{ display: flex; justify-content: space-between; align-items: center; gap: 1rem; padding: 14px 16px;
          border-bottom: 1px solid var(--line); background: var(--surface); }}
        .brand {{ font-weight: 800; letter-spacing: .12em; font-size: .9rem; }}
        .label {{ padding: .1rem .55rem; border: 1px solid var(--highlight); border-radius: 999px; color: var(--highlight);
          font-size: .7rem; letter-spacing: .08em; text-transform: uppercase; white-space: nowrap; }}
        main {{ flex: 1; display: grid; place-items: center; padding: 32px 16px; }}
        .card {{ max-width: 34rem; width: 100%; text-align: center; }}
        svg {{ width: 88px; height: 88px; margin-bottom: 1.25rem; }}
        h1 {{ margin: 0 0 1rem; font-size: clamp(1.4rem, 5vw, 2rem); letter-spacing: .06em; }}
        p {{ margin: 0 auto 1.5rem; max-width: 30rem; color: var(--muted); }}
        .eta {{ display: inline-block; margin-bottom: 1.75rem; padding: .5rem 1rem; border: 1px dashed var(--accent);
          border-radius: 8px; color: var(--accent); font-weight: 700; letter-spacing: .04em; }}
        .cta {{ display: block; width: fit-content; margin: 0 auto; padding: .75rem 1.5rem; border-radius: 8px; background: var(--accent);
          color: var(--bg); font-weight: 800; letter-spacing: .08em; text-decoration: none; }}
        .cta:focus-visible {{ outline: 3px solid var(--highlight); outline-offset: 3px; }}
        </style>
        </head>
        <body>
        <header>
        <span class="brand">TOUCHLINE MATCH CENTRE</span>
        <span class="label">Synthetic data</span>
        </header>
        <main>
        <div class="card">
        <svg viewBox="0 0 64 64" role="img" aria-label="Traffic cone">
        <path d="M27 8h10l13 44H14z" fill="var(--accent)"/>
        <path d="M23.5 22h17l2.4 8H21.1zM19.4 36h25.2l2.4 8H17z" fill="var(--text)"/>
        <rect x="8" y="52" width="48" height="6" rx="2" fill="var(--muted)"/>
        </svg>
        <h1>PITCH MAINTENANCE IN PROGRESS</h1>
        <p>We're rolling the turf, testing the VAR synthetic simulation engines, and watering the grass for the next round of matches.</p>
        <div class="eta">Kick-off expected in roughly {1} mins.</div>
        <a class="cta" href="/">RETURN TO TOUCHLINE</a>
        </div>
        </main>
        </body>
        </html>
        """;

    /// <summary>The page, reloading itself every <paramref name="refreshSeconds"/> seconds.</summary>
    /// <param name="refreshSeconds">How often the page reloads.</param>
    /// <param name="estimatedMinutes">Roughly how long until the Match Centre is back.</param>
    /// <returns>The HTML.</returns>
    public static string Html(int refreshSeconds, int estimatedMinutes) =>
        string.Format(CultureInfo.InvariantCulture, _template, refreshSeconds, estimatedMinutes);
}
