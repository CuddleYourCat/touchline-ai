import { useEffect, useState } from 'react';
import type { OverlayCard } from '../live/matchStreamReducer';

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

  if (!active) return null;
  const { title, subtitle, team } = active.payload;
  const teamName = team === 'Home' ? homeName : team === 'Away' ? awayName : null;
  const className = ['overlay-toast', team ? `overlay-${team.toLowerCase()}` : '', fading === active.id ? 'overlay-fading' : '']
    .filter(Boolean)
    .join(' ');

  return (
    <div className={className} role="status" aria-label="Overlay" data-type={active.type}>
      <span className="overlay-clock data">{active.at.display}</span>
      <strong className="overlay-title">{title}</strong>
      {subtitle && <span className="overlay-subtitle">{subtitle}</span>}
      {teamName && <span className="overlay-team muted">{teamName}</span>}
    </div>
  );
}
