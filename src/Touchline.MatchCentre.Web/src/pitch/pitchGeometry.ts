import { hasBall, pitchWidth, type BallPath, type MatchEvent, type PitchPosition } from '../live/matchStreamReducer';

/** Pitch Y runs up from the bottom touchline; SVG Y runs down, so it's flipped. X is unchanged: home attacks left to right. */
export function toSvg(p: PitchPosition): { x: number; y: number } {
  return { x: p.x, y: pitchWidth - p.y };
}

const round = (n: number) => Math.round(n * 100) / 100;

/**
 * The SVG path for a ball path, or null when the ball stayed still. A pass bends into an arc (a quadratic curve whose
 * control point sits off the midpoint, at right angles to the line); a carry and a shot run straight.
 */
export function ballPathD(path: BallPath): string | null {
  if (path.kind === 'still') return null;
  const from = toSvg(path.from);
  const to = toSvg(path.to);
  if (path.kind !== 'pass') return `M ${round(from.x)} ${round(from.y)} L ${round(to.x)} ${round(to.y)}`;
  const dx = to.x - from.x;
  const dy = to.y - from.y;
  const length = Math.hypot(dx, dy) || 1;
  const bend = Math.min(length * 0.2, 10);
  const cx = (from.x + to.x) / 2 - (dy / length) * bend;
  const cy = (from.y + to.y) / 2 + (dx / length) * bend;
  return `M ${round(from.x)} ${round(from.y)} Q ${round(cx)} ${round(cy)} ${round(to.x)} ${round(to.y)}`;
}

/**
 * Where the ball was after each of the few events before the latest, oldest first, for the fading trail. A
 * substitution has no ball in it, so it leaves no point.
 */
export function ballTrail(events: MatchEvent[], length = 5): PitchPosition[] {
  return events
    .filter(hasBall)
    .slice(-(length + 1), -1)
    .map((e) => e.end ?? e.start);
}

/**
 * The fading trail as one joined line, drawn in layers. Layer `k` runs from point `k` to the newest point, and the
 * layers are stacked at the same low opacity, so the newest stretch (under every layer) is the strongest and the
 * oldest (under one) the faintest. Each layer is a single path, so the joins stay clean. Repeated points are
 * dropped first; fewer than two distinct points draw nothing.
 */
export function trailLayers(points: PitchPosition[]): string[] {
  const svg = points
    .map(toSvg)
    .filter((p, i, all) => i === 0 || p.x !== all[i - 1]!.x || p.y !== all[i - 1]!.y);
  if (svg.length < 2) return [];
  return svg.slice(0, -1).map((_, k) =>
    svg
      .slice(k)
      .map((p, i) => `${i === 0 ? 'M' : 'L'} ${round(p.x)} ${round(p.y)}`)
      .join(' '),
  );
}

/** A foul or card to mark on the pitch. */
export interface IncidentMarker {
  sequence: number;
  kind: 'foul' | 'yellow' | 'red';
  at: PitchPosition;
}

/** Minutes on an `mm:ss` match clock, with the seconds as a fraction. */
function clockMinutes(clock: string): number {
  const [minutes = '0', seconds = '0'] = clock.split(':');
  return Number(minutes) + Number(seconds) / 60;
}

/**
 * How many match minutes `later` came after `earlier`. Once the second half is under way, first-half added time counts
 * as minute 45, because the second-half clock restarts at 45:00.
 */
function minutesBetween(earlier: MatchEvent['time'], later: MatchEvent['time']): number {
  const from = clockMinutes(earlier.clock);
  const to = clockMinutes(later.clock);
  return to - (earlier.period === 'FirstHalf' && later.period !== 'FirstHalf' ? Math.min(from, 45) : from);
}

/**
 * The recent fouls and cards, oldest first: at most `limit` of them, and none from more than `withinMinutes` match
 * minutes before the latest event.
 */
export function incidentMarkers(events: MatchEvent[], limit = 8, withinMinutes = 5): IncidentMarker[] {
  const latest = events.at(-1);
  if (!latest) return [];
  const markers: IncidentMarker[] = [];
  for (const e of events) {
    if (minutesBetween(e.time, latest.time) > withinMinutes) continue;
    if (e.type === 'Foul') markers.push({ sequence: e.sequence, kind: 'foul', at: e.start });
    else if (e.type === 'Card' && e.card) markers.push({ sequence: e.sequence, kind: e.card === 'Red' ? 'red' : 'yellow', at: e.start });
  }
  return markers.slice(-limit);
}

/**
 * The dot label for a player in neither line-up: the number at the end of their ID (`nbfc-07` gives `7`), or `?`.
 * The raw ID is never shown.
 */
export function unknownPlayerLabel(playerId: string): string {
  const digits = /(\d+)$/.exec(playerId)?.[1];
  return digits ? String(Number(digits)) : '?';
}

/**
 * True when the ball path belongs to an event replayed after a reconnect: its sequence is more than one behind the
 * latest shape's. A live event arrives with, or just before, its own shape, so it's never that far behind.
 */
export function isReplayed(ballPath: BallPath, shape: { sequence: number } | null): boolean {
  return shape !== null && ballPath.sequence < shape.sequence - 1;
}
