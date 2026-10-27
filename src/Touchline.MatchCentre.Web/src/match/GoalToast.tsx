import { useEffect, useRef, useState } from 'react';
import type { OverlayCard } from '../live/matchStreamReducer';
import { useReducedMotion } from '../pitch/useReducedMotion';
import './GoalToast.css';

/** How long the fade-out takes once the card's time is up. */
export const goalToastFadeMs = 500;
const minimumShowMs = 3000;
const defaultShowMs = 4000;

export interface GoalToastProps {
  /** A card with type "goal", or null when there is nothing to show. */
  card: OverlayCard | null;
  scorerName: string | null;
  teamName: string;
  score: { home: number; away: number; homeName: string; awayName: string };
  /** Already formatted, e.g. `68'`. */
  minute: string;
  replay?: boolean;
  onDone: () => void;
}

/**
 * A goal celebrated large in the centre of the pitch (ADR-068). Plain white text with a shadow, no box and no team
 * colours (ADR-071). It shows for the card's duration, fades, then calls
 * `onDone`. A new card replaces the current one and restarts the timer. The card is `aria-hidden`; a live region that
 * stays mounted does the announcing, and nothing here takes clicks.
 */
export function GoalToast({ card, scorerName, teamName, score, minute, replay, onDone }: GoalToastProps) {
  const reducedMotion = useReducedMotion();
  // Keyed on the card object, not its id: a replay delivers the same ids again in fresh objects.
  const [fading, setFading] = useState<OverlayCard | null>(null);
  const [dismissed, setDismissed] = useState<OverlayCard | null>(null);
  const showMs = !card ? 0 : card.durationMs < minimumShowMs ? defaultShowMs : card.durationMs;

  // The timer is keyed on the card, not the callback, so a parent re-rendering with a new function doesn't restart it.
  const onDoneRef = useRef(onDone);
  useEffect(() => {
    onDoneRef.current = onDone;
  }, [onDone]);

  useEffect(() => {
    if (!card) return;
    const fadeTimer = setTimeout(() => setFading(card), showMs);
    const doneTimer = setTimeout(() => {
      setDismissed(card);
      onDoneRef.current();
    }, showMs + goalToastFadeMs);
    return () => {
      clearTimeout(fadeTimer);
      clearTimeout(doneTimer);
    };
  }, [card, showMs]);

  // The live region is always mounted, so a screen reader is already listening when a goal arrives.
  const announce = card
    ? ['Goal', teamName, scorerName, `${minute.replace(/'$/, '')} minutes`, `${score.home}–${score.away}`]
        .filter(Boolean)
        .join(', ')
    : '';
  const announcer = (
    <div className="visually-hidden" role="status" aria-label="Goal announcements">
      {announce}
    </div>
  );

  if (!card || dismissed === card) return announcer;

  const className = [
    'goal-toast',
    reducedMotion ? 'goal-toast-still' : 'goal-toast-animated',
    fading === card ? 'goal-toast-fading' : '',
  ]
    .filter(Boolean)
    .join(' ');

  return (
    <>
      {announcer}
      <div className={className} aria-hidden="true" data-testid="goal-toast">
        {replay && <span className="goal-toast-replay">Replay</span>}
        <strong className="goal-toast-title">{card.payload.title}</strong>
        {scorerName && <span className="goal-toast-scorer">{scorerName}</span>}
        <span className="goal-toast-team">{teamName}</span>
        <span className="goal-toast-score data">
          {score.homeName} {score.home} – {score.away} {score.awayName}
        </span>
        <span className="goal-toast-minute data">{minute}</span>
      </div>
    </>
  );
}
