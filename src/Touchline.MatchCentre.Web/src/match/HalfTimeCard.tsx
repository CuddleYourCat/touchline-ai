import type { LiveState, MatchDetail, MomentumReading } from '../live/matchStreamReducer';
import { useReducedMotion } from '../pitch/useReducedMotion';
import './HalfTimeCard.css';

export interface HalfTimeCardProps {
  /** Team names: `detail.summary.home.name` / `away.name`, and the short names for narrow screens. */
  detail: MatchDetail;
  /** The score and each side's stats at half time. */
  state: LiveState;
  /** All readings so far; only the first half's are used. */
  momentum: MomentumReading[];
  /** True when the match is being replayed rather than watched live. */
  replay?: boolean;
  /**
   * 'half' (the default) is the half-time pause over the pitch; 'full' is the same numbers at full time, laid out in
   * the page flow (the recap, ADR-071 amendment 4) and summarising every reading rather than the first half's.
   */
  phase?: 'half' | 'full';
}

/** A side "had the better of" the half when it led in more than this share of the first half's readings. */
const dominanceThreshold = 0.6;

type TeamStats = LiveState['home'];

interface Row {
  key: string;
  label: string;
  home: number;
  away: number;
  format: (value: number) => string;
}

const whole = (value: number) => String(value);
const oneDecimal = (value: number) => value.toFixed(1);
/** `passAccuracy` is a 0-1 fraction (see the contract), but a value above 1 is read as a percentage already. */
const percent = (value: number) => `${Math.round(value <= 1 ? value * 100 : value)}%`;

function rowsOf(home: TeamStats, away: TeamStats): Row[] {
  const rows: Row[] = [
    { key: 'shots', label: 'Shots', home: home.shots, away: away.shots, format: whole },
    { key: 'sot', label: 'Shots on target', home: home.shotsOnTarget, away: away.shotsOnTarget, format: whole },
    { key: 'xg', label: 'xG', home: home.expectedGoals, away: away.expectedGoals, format: oneDecimal },
    {
      key: 'pass',
      label: 'Pass accuracy',
      home: home.passAccuracy ?? 0,
      away: away.passAccuracy ?? 0,
      format: percent,
    },
    { key: 'fouls', label: 'Fouls', home: home.fouls, away: away.fouls, format: whole },
    { key: 'yellow', label: 'Yellow cards', home: home.yellowCards, away: away.yellowCards, format: whole },
  ];
  if (home.redCards > 0 || away.redCards > 0) {
    rows.push({ key: 'red', label: 'Red cards', home: home.redCards, away: away.redCards, format: whole });
  }
  return rows;
}

/**
 * Which side had the upper hand in the first half, by the share of its readings each side led (the sign convention of
 * `MomentumBars`: above zero is home, below is away, zero is level). A side needs more than `dominanceThreshold` of
 * the readings; otherwise the half was even. Null when there are no first-half readings.
 */
function momentumSummary(
  momentum: MomentumReading[],
  homeName: string,
  awayName: string,
  phase: 'half' | 'full',
): string | null {
  const counted = phase === 'half' ? momentum.filter((r) => r.period === 'FirstHalf') : momentum;
  if (counted.length === 0) return null;
  const span = phase === 'half' ? 'the first half' : 'the match';
  const homeShare = counted.filter((r) => r.value > 0).length / counted.length;
  const awayShare = counted.filter((r) => r.value < 0).length / counted.length;
  if (homeShare > dominanceThreshold) return `${homeName} had the better of ${span}`;
  if (awayShare > dominanceThreshold) return `${awayName} had the better of ${span}`;
  return phase === 'half' ? 'An even first half' : 'An even match';
}

/** The home side's share of the pair, as a percentage; an even split when both are nil. */
function homeShareOf(home: number, away: number): number {
  const total = home + away;
  return total > 0 ? (home / total) * 100 : 50;
}

/**
 * A numbers-only half-time card for the half-time pause (ADR-066). It sits over the pitch, so its container must be
 * positioned. It is informational and never takes focus.
 */
export function HalfTimeCard({ detail, state, momentum, replay = false, phase = 'half' }: HalfTimeCardProps) {
  const full = phase === 'full';
  const reducedMotion = useReducedMotion();
  const { home, away } = detail.summary;
  const rows = rowsOf(state.home, state.away);
  const summary = momentumSummary(momentum, home.name, away.name, phase);

  return (
    <section
      className={['half-time-card', reducedMotion && 'half-time-still', full && 'half-time-card-static']
        .filter(Boolean)
        .join(' ')}
      role="region"
      tabIndex={0}
      aria-label={full ? 'Full-time stats' : 'Half-time stats'}
    >
      <header className="half-time-head">
        <h2 className="half-time-title">{full ? 'Full time' : 'Half time'}</h2>
        {replay && <span className="half-time-replay">Replay</span>}
      </header>
      <p className="half-time-score data">
        <span className="half-time-team">
          <span className="half-time-full">{home.name}</span>
          <span className="half-time-short">
            {home.shortName}
          </span>
        </span>
        <strong className="half-time-goals">
          {state.score.home}-{state.score.away}
        </strong>
        <span className="half-time-team">
          <span className="half-time-full">{away.name}</span>
          <span className="half-time-short">
            {away.shortName}
          </span>
        </span>
      </p>
      {summary && <p className="half-time-momentum">{summary}</p>}
      <table className="half-time-table">
        <thead>
          <tr>
            <th scope="col" className="half-time-home-head">
              {home.shortName}
            </th>
            <th scope="col" className="half-time-stat-head">
              Stat
            </th>
            <th scope="col" className="half-time-away-head">
              {away.shortName}
            </th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => {
            const share = homeShareOf(row.home, row.away);
            return (
              <tr key={row.key} data-testid="half-time-row">
                <td className="half-time-value data">{row.format(row.home)}</td>
                <th scope="row" className="half-time-label">
                  {row.label}
                  <span className="half-time-bar" aria-hidden="true">
                    <span className="half-time-bar-home" style={{ width: `${share}%` }} />
                    <span className="half-time-bar-away" style={{ width: `${100 - share}%` }} />
                  </span>
                </th>
                <td className="half-time-value data">{row.format(row.away)}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </section>
  );
}
