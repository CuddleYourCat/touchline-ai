import { useEffect, useState } from 'react';
import type { OverlayCard } from '../live/matchStreamReducer';
import { stripClockPrefix } from '../match/playerFacts';
import { clockLabel } from '../matches/clockLabel';

/** How long the fade-out takes once a card's time is up. */
export const overlayFadeMs = 400;

/**
 * The newest overlay card as a toast over the pitch. It shows for the card's `durationMs`, fades, then goes. A card
 * already dismissed (say, replayed after a reconnect) doesn't come back, and an older card never shows once a newer one
 * has arrived.
 */
export function OverlayToast({ overlays, homeName, awayName }: { overlays: OverlayCard[]; homeName: string; awayName: string }) {
  const [fading, setFading] = useState<string | null>(null);
  const [dismissed, setDismissed] = useState<ReadonlySet<string>>(() => new Set());
  const latest = overlays.at(-1);
  const active = latest && !dismissed.has(latest.id) ? latest : undefined;
  const activeId = active?.id;
  const durationMs = active?.durationMs ?? 0;

  useEffect(() => {
    if (!activeId) return;
    const fadeTimer = setTimeout(() => setFading(activeId), durationMs);
    const goneTimer = setTimeout(() => setDismissed((d) => new Set(d).add(activeId)), durationMs + overlayFadeMs);
    return () => {
      clearTimeout(fadeTimer);
      clearTimeout(goneTimer);
    };
  }, [activeId, durationMs]);

  // The live region is always mounted, so a screen reader is already listening when a card arrives; a region that
  // appears with its text already in it is often not announced. The newest card's words go in it as it arrives.
  const announce = latest ? announcement(latest, homeName, awayName) : '';
  const announcer = (
    <div className="visually-hidden" role="status" aria-label="Overlay announcements">
      {announce}
    </div>
  );

  if (!active) return announcer;
  const { title, subtitle, team } = active.payload;
  const teamName = teamNameOf(team, homeName, awayName);
  const className = ['overlay-toast', team ? `overlay-${team.toLowerCase()}` : '', fading === active.id ? 'overlay-fading' : '']
    .filter(Boolean)
    .join(' ');

  return (
    <>
      {announcer}
      <div className={className} aria-hidden="true" data-testid="overlay-toast" data-type={active.type}>
        <span className="overlay-clock data">{clockLabel(active.at)}</span>
        <strong className="overlay-title">{stripClockPrefix(title)}</strong>
        {subtitle && <span className="overlay-subtitle">{stripClockPrefix(subtitle)}</span>}
        {teamName && <span className="overlay-team muted">{teamName}</span>}
      </div>
    </>
  );
}

function teamNameOf(team: OverlayCard['payload']['team'], homeName: string, awayName: string): string | null {
  return team === 'Home' ? homeName : team === 'Away' ? awayName : null;
}

/** What a screen reader hears for a card: `23:04, GOAL, Ashby 1-0, Harbourside Rovers`. */
function announcement(card: OverlayCard, homeName: string, awayName: string): string {
  const { title, subtitle, team } = card.payload;
  return [clockLabel(card.at), stripClockPrefix(title), subtitle && stripClockPrefix(subtitle), teamNameOf(team, homeName, awayName)].filter(Boolean).join(', ');
}
