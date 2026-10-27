import { useEffect, useRef, useState, type KeyboardEvent } from 'react';
import { api } from '../api/client';
import type { components } from '../api/schema';
import type { MatchDetail } from '../live/matchStreamReducer';
import { useReducedMotion } from '../pitch/useReducedMotion';
import { MomentIcon } from './MomentIcon';
import { momentIconLabel } from './momentIcons';
import { countOf, footballMinute, type PlayerFact } from './playerFacts';
import './PlayerFlyout.css';

type PlayerSummary = MatchDetail['homeLineup'][number];
type PlayerSeason = components['schemas']['PlayerSeason'];

export interface PlayerFlyoutProps {
  /** The player to show, or null when the flyout is closed. */
  player: PlayerSummary | null;
  teamName: string;
  facts: PlayerFact[] | undefined;
  /** The match's season (`detail.summary.season`); null for exhibitions and fan matches. */
  season: string | null;
  onClose: () => void;
  /** Called with a fact's moment ID, so the page can replay from it (ADR-067). */
  onSelectMoment: (momentId: string) => void;
  /**
   * True while the line-ups drawer is open beside the card. The card then has no backdrop and doesn't trap focus, so
   * clicking another player in the drawer switches the card to that player (ADR-071).
   */
  withDrawer?: boolean;
}

type SeasonState =
  | { key: string; status: 'ready'; stats: PlayerSeason }
  | { key: string; status: 'none' }
  | { key: string; status: 'error' };

const focusable = 'button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])';

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/** "2 goals · 1 yellow card": only the kinds with a count, or an empty string. */
function tallyText(facts: PlayerFact[]): string {
  const parts: [number, string, string][] = [
    [countOf(facts, 'goal'), 'goal', 'goals'],
    [countOf(facts, 'assist'), 'assist', 'assists'],
    [countOf(facts, 'yellow'), 'yellow card', 'yellow cards'],
    [countOf(facts, 'red'), 'red card', 'red cards'],
  ];
  return parts
    .filter(([n]) => n > 0)
    .map(([n, one, many]) => plural(n, one, many))
    .join(' · ');
}

/** A flyout with a player's name, this match and season stats, opened from the line-ups card (ADR-068). */
export function PlayerFlyout({ player, teamName, facts, season, onClose, onSelectMoment, withDrawer = false }: PlayerFlyoutProps) {
  const open = player !== null;
  const dialogRef = useRef<HTMLDivElement>(null);
  const closeRef = useRef<HTMLButtonElement>(null);
  const onCloseRef = useRef(onClose);
  const reduced = useReducedMotion();
  const [attempt, setAttempt] = useState(0);
  const [loaded, setLoaded] = useState<SeasonState | null>(null);

  useEffect(() => {
    onCloseRef.current = onClose;
  }, [onClose]);

  // Focus the close button on open, and hand focus back to whatever opened the flyout on close.
  useEffect(() => {
    if (!open) {
      return;
    }

    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    closeRef.current?.focus();
    const onKey = (e: globalThis.KeyboardEvent) => {
      // Something above the card (the settings popover) may already have taken this Escape.
      if (e.key === 'Escape' && !e.defaultPrevented) {
        e.stopPropagation();
        onCloseRef.current();
      }
    };
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('keydown', onKey);
      opener?.focus();
    };
  }, [open]);

  const playerId = player?.id;
  const seasonKey = playerId && season !== null ? `${season}/${playerId}/${attempt}` : null;
  useEffect(() => {
    if (!playerId || season === null || seasonKey === null) {
      return;
    }

    const controller = new AbortController();
    api
      .GET('/api/seasons/{season}/players/{playerId}', {
        params: { path: { season, playerId } },
        signal: controller.signal,
      })
      .then(({ data, response }) => {
        if (controller.signal.aborted) {
          return;
        }

        if (data) {
          setLoaded({ key: seasonKey, status: 'ready', stats: data });
        } else {
          setLoaded({ key: seasonKey, status: response.status === 404 ? 'none' : 'error' });
        }
      })
      .catch(() => {
        if (!controller.signal.aborted) {
          setLoaded({ key: seasonKey, status: 'error' });
        }
      });
    return () => controller.abort();
  }, [playerId, season, seasonKey]);

  if (!player) {
    return null;
  }

  const trapTab = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key !== 'Tab' || !dialogRef.current || withDrawer) {
      return;
    }

    const items = [...dialogRef.current.querySelectorAll<HTMLElement>(focusable)].filter(
      (el) => !el.hasAttribute('disabled'),
    );
    if (items.length === 0) {
      return;
    }

    const first = items[0]!;
    const last = items[items.length - 1]!;
    const active = document.activeElement;
    if (e.shiftKey && (active === first || !dialogRef.current.contains(active))) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && (active === last || !dialogRef.current.contains(active))) {
      e.preventDefault();
      first.focus();
    }
  };

  const state = loaded && loaded.key === seasonKey ? loaded : null;
  const titleId = `flyout-title-${player.id}`;
  const list = facts ?? [];
  const tally = tallyText(list);

  return (
    <div
      className={withDrawer ? 'flyout-backdrop flyout-backdrop-clear' : 'flyout-backdrop'}
      onMouseDown={(e) => e.target === e.currentTarget && onClose()}
    >
      <div
        ref={dialogRef}
        className={reduced ? 'flyout flyout--still' : 'flyout'}
        role="dialog"
        aria-modal={!withDrawer}
        aria-labelledby={titleId}
        onKeyDown={trapTab}
      >
        <header className="flyout-header">
          <span className="flyout-number" aria-label={`Shirt number ${player.shirtNumber}`}>
            {player.shirtNumber}
          </span>
          <div className="flyout-who">
            <h2 id={titleId} className="flyout-name">
              {player.name}
            </h2>
            <p className="flyout-sub">
              {player.position} · {teamName}
            </p>
          </div>
          <button ref={closeRef} type="button" className="flyout-close" onClick={onClose} aria-label="Close">
            <span aria-hidden="true">×</span>
          </button>
        </header>

        <section className="flyout-section" aria-labelledby={`${titleId}-match`}>
          <h3 id={`${titleId}-match`} className="flyout-heading">
            This match
          </h3>
          {list.length === 0 ? (
            <p className="flyout-note">Nothing yet</p>
          ) : (
            <>
              {tally && <p className="flyout-tally">{tally}</p>}
              <ul className="flyout-facts">
                {list.map((fact) => (
                  <li key={`${fact.momentId}-${fact.kind}`}>
                    <button type="button" className="flyout-fact" onClick={() => onSelectMoment(fact.momentId)}>
                      <MomentIcon kind={fact.kind} />
                      <span>{momentIconLabel[fact.kind]}</span>
                      <span className="flyout-minute">{footballMinute(fact.clock)}</span>
                      <span className="flyout-sr">, replay from here</span>
                    </button>
                  </li>
                ))}
              </ul>
            </>
          )}
        </section>

        <section className="flyout-section" aria-labelledby={`${titleId}-season`}>
          <h3 id={`${titleId}-season`} className="flyout-heading">
            Season
          </h3>
          {season === null ? (
            <p className="flyout-note">Season stats are kept for league fixtures only. This match isn&apos;t one.</p>
          ) : !state ? (
            <p className="flyout-note" role="status">
              Loading season stats…
            </p>
          ) : state.status === 'none' ? (
            <p className="flyout-note">No season stats yet</p>
          ) : state.status === 'error' ? (
            <p className="flyout-note" role="alert">
              Couldn&apos;t load season stats.{' '}
              <button type="button" className="flyout-retry" onClick={() => setAttempt((n) => n + 1)}>
                Try again
              </button>
            </p>
          ) : (
            <SeasonStats stats={state.stats} />
          )}
        </section>
      </div>
    </div>
  );
}

function SeasonStats({ stats }: { stats: PlayerSeason }) {
  const completion = stats.passes > 0 ? `${Math.round((stats.passesCompleted / stats.passes) * 100)}%` : '–';
  const rows: [string, string][] = [
    ['Appearances', String(stats.appearances)],
    ['As substitute', String(stats.substituteAppearances)],
    ['Goals', String(stats.goals)],
    ['Assists', String(stats.assists)],
    ['Shots', String(stats.shots)],
    ['Shots on target', String(stats.shotsOnTarget)],
    ['xG', stats.expectedGoals.toFixed(1)],
    ['Passes', String(stats.passes)],
    ['Pass completion', completion],
    ['Fouls', String(stats.fouls)],
    ['Yellow cards', String(stats.yellowCards)],
    ['Red cards', String(stats.redCards)],
  ];
  if (stats.bannedRounds.length > 0) {
    rows.push([stats.bannedRounds.length === 1 ? 'Banned round' : 'Banned rounds', stats.bannedRounds.join(', ')]);
  }

  return (
    <dl className="flyout-stats">
      {rows.map(([label, value]) => (
        <div key={label} className="flyout-stat">
          <dt>{label}</dt>
          <dd>{value}</dd>
        </div>
      ))}
    </dl>
  );
}
