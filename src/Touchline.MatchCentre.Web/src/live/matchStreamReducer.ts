import type { components } from '../api/schema';

type Schemas = components['schemas'];

/** A match in full, as `GET /api/matches/{id}` returns it. */
export type MatchDetail = Schemas['MatchDetail'];
/** The score, clock and totals at a point in a match. */
export type LiveState = Schemas['LiveState'];
/** One match event. */
export type MatchEvent = Schemas['MatchEvent'];
/** A key or notable moment. */
export type MomentView = Schemas['MomentView'];
/** One line of commentary about a moment. */
export type CommentaryLine = Schemas['CommentaryLine'];
/** The Analyst's explanation of a key moment. */
export type MomentAnalysis = Schemas['MomentAnalysis'];
/** A milestone, such as a first goal. */
export type MilestoneView = Schemas['MilestoneView'];
/** One five-minute momentum window. */
export type MomentumReading = Schemas['MomentumReading'];
/** A timecoded overlay card. */
export type OverlayCard = Schemas['OverlayCard'];
/** The modelled positions of every player on the pitch after one event (ADR-035). Not tracking data. */
export type ShapeView = Schemas['ShapeView'];
/** A point on the pitch, in metres. */
export type PitchPosition = Schemas['PitchPosition'];

/**
 * Where the ball went in one event, ready to draw. A pass arcs, a carry runs straight and a shot heads for goal;
 * anything else leaves the ball still where it started.
 */
export interface BallPath {
  sequence: number;
  kind: 'pass' | 'carry' | 'shot' | 'still';
  team: MatchEvent['team'];
  from: PitchPosition;
  to: PitchPosition;
}

/** The pitch is 105 m long and 68 m wide. The home side attacks the goal at X = 105. */
export const pitchLength = 105;
/** The pitch width in metres. */
export const pitchWidth = 68;

/** Half the goal's width in metres: the posts are 7.32 m apart. */
const halfGoal = 3.66;

/**
 * Where a shot's ball ends up, by its outcome. A goal goes to its end point or the goal's centre. A save stops on the
 * goal line between the posts. A block stops at its end point, or about 2 m on towards goal. A miss goes to its end
 * point, or wide of the near post on the goal line. A shot with no detail goes to its end point or the goal's centre.
 */
export function shotTarget(event: MatchEvent): PitchPosition {
  const goalX = event.team === 'Home' ? pitchLength : 0;
  const centre = { x: goalX, y: pitchWidth / 2 };
  const end = event.end ?? null;
  switch (event.shot?.outcome) {
    case 'Saved': {
      const y = end?.y ?? centre.y;
      return { x: goalX, y: Math.min(Math.max(y, centre.y - halfGoal), centre.y + halfGoal) };
    }
    case 'Blocked': {
      if (end) return end;
      const dx = centre.x - event.start.x;
      const dy = centre.y - event.start.y;
      const length = Math.hypot(dx, dy) || 1;
      const step = Math.min(2, length);
      return { x: event.start.x + (dx / length) * step, y: event.start.y + (dy / length) * step };
    }
    case 'OffTarget': {
      if (end) return end;
      const side = event.start.y >= centre.y ? 1 : -1;
      return { x: goalX, y: centre.y + side * (halfGoal + 2) };
    }
    default:
      return end ?? centre;
  }
}

/** The ball path for an event. A shot's end depends on how it ended (see {@link shotTarget}). */
export function ballPathOf(event: MatchEvent): BallPath {
  const base = { sequence: event.sequence, team: event.team, from: event.start };
  switch (event.type) {
    case 'Pass':
      return event.end ? { ...base, kind: 'pass', to: event.end } : { ...base, kind: 'still', to: event.start };
    case 'Carry':
      return event.end ? { ...base, kind: 'carry', to: event.end } : { ...base, kind: 'still', to: event.start };
    case 'Shot':
      return { ...base, kind: 'shot', to: shotTarget(event) };
    default:
      return { ...base, kind: 'still', to: event.start };
  }
}

/** False for an event with nothing to do with the ball: a substitution (ADR-064) starts on the touchline at halfway. */
export function hasBall(event: MatchEvent): boolean {
  return event.type !== 'Substitution';
}

/** How many recent events the live view keeps; older ones fall off the front. */
export const maxEvents = 200;

/** Every stream message, by its `event:` name (ADR-027). Heartbeats only reset the staleness timer. */
export type StreamMessage =
  | { type: 'state'; data: LiveState }
  | { type: 'event'; data: MatchEvent }
  | { type: 'moment'; data: MomentView }
  | { type: 'commentary'; data: CommentaryLine }
  | { type: 'analysis'; data: MomentAnalysis }
  | { type: 'milestone'; data: MilestoneView }
  | { type: 'momentum'; data: MomentumReading }
  | { type: 'overlay'; data: OverlayCard }
  | { type: 'shape'; data: ShapeView }
  | { type: 'fulltime'; data: LiveState };

/** The stream event names the hook listens for, heartbeat included. */
export const streamEventNames = [
  'state',
  'event',
  'moment',
  'commentary',
  'analysis',
  'milestone',
  'momentum',
  'overlay',
  'shape',
  'fulltime',
  'heartbeat',
] as const;

/** Everything the live view knows about one match. */
export interface LiveMatch {
  detail: MatchDetail;
  state: LiveState;
  /** The most recent events, oldest first, at most {@link maxEvents}. */
  events: MatchEvent[];
  moments: Record<string, MomentView>;
  /** The best line so far for each moment, by moment ID. */
  commentary: Record<string, CommentaryLine>;
  analysis: Record<string, MomentAnalysis>;
  milestones: MilestoneView[];
  momentum: MomentumReading[];
  overlays: OverlayCard[];
  /** The latest modelled positions, or null before the first `shape` message. */
  shape: ShapeView | null;
  /** Where the ball went in the latest event, or null before the first event. */
  ballPath: BallPath | null;
  fullTime: boolean;
  /** The viewer's language (a BCP 47 tag such as `es-ES`), which ranks competing lines. */
  language: string;
}

/** The starting point once the detail and the moments so far have loaded. */
/**
 * The detail with both benches present. A server from before substitutions (ADR-064), such as an older deployment or a
 * cached response, leaves them out, and every panel that lists players expects an array.
 */
export function withBenches(detail: MatchDetail): MatchDetail {
  return detail.homeSubstitutes && detail.awaySubstitutes
    ? detail
    : { ...detail, homeSubstitutes: detail.homeSubstitutes ?? [], awaySubstitutes: detail.awaySubstitutes ?? [] };
}

export function initialLiveMatch(detail: MatchDetail, moments: MomentView[], language = 'en'): LiveMatch {
  const live: LiveMatch = {
    detail: withBenches(detail),
    state: detail.state,
    events: [],
    moments: {},
    commentary: {},
    analysis: {},
    milestones: [],
    momentum: [],
    overlays: [],
    shape: null,
    ballPath: null,
    fullTime: detail.state.fullTime,
    language,
  };
  return mergeMoments(live, moments);
}

/** Adds moments fetched over HTTP. Moments are keyed by ID, so one already seen on the stream isn't duplicated. */
export function mergeMoments(live: LiveMatch, moments: MomentView[]): LiveMatch {
  return moments.reduce(addMoment, live);
}

const baseLanguage = (tag: string) => (tag.split('-')[0] ?? tag).toLowerCase();

/** 0 for the viewer's exact language, 1 for its base language, 2 for English, 3 for anything else. */
function languageRank(lineLanguage: string, viewerLanguage: string): number {
  if (lineLanguage.toLowerCase() === viewerLanguage.toLowerCase()) return 0;
  if (baseLanguage(lineLanguage) === baseLanguage(viewerLanguage)) return 1;
  return baseLanguage(lineLanguage) === 'en' ? 2 : 3;
}

/**
 * Picks the line to show. Language comes first: a line in a worse language for the viewer never replaces one in a
 * better language, whatever its source. Within the same rank a model line beats a template line; otherwise the newer
 * line wins.
 */
export function betterLine(current: CommentaryLine | undefined, next: CommentaryLine, viewerLanguage: string): CommentaryLine {
  if (!current) return next;
  const currentRank = languageRank(current.language, viewerLanguage);
  const nextRank = languageRank(next.language, viewerLanguage);
  if (nextRank !== currentRank) return nextRank < currentRank ? next : current;
  return current.source === 'Model' && next.source === 'Template' ? current : next;
}

/** Replaces the item with the same key, or appends it, so a replay after a reconnect doesn't duplicate. */
function upsert<T>(items: T[], item: T, key: (t: T) => string): T[] {
  const k = key(item);
  const index = items.findIndex((t) => key(t) === k);
  if (index < 0) return [...items, item];
  const copy = [...items];
  copy[index] = item;
  return copy;
}

function withLine(live: LiveMatch, line: CommentaryLine): LiveMatch {
  const current = live.commentary[line.momentId];
  const best = betterLine(current, line, live.language);
  return best === current ? live : { ...live, commentary: { ...live.commentary, [line.momentId]: best } };
}

function addMoment(live: LiveMatch, moment: MomentView): LiveMatch {
  const next = { ...live, moments: { ...live.moments, [moment.id]: moment } };
  return moment.commentary ? withLine(next, moment.commentary) : next;
}

/** Applies one stream message. Pure, so it's tested without a stream. */
export function applyMessage(live: LiveMatch, message: StreamMessage): LiveMatch {
  switch (message.type) {
    case 'state':
      return { ...live, state: message.data };
    case 'fulltime':
      return { ...live, state: message.data, fullTime: true };
    case 'event': {
      const events = [...live.events, message.data];
      return {
        ...live,
        events: events.length > maxEvents ? events.slice(-maxEvents) : events,
        // A substitution (ADR-064) happens on the touchline with the ball dead, so the ball stays where it was.
        ballPath: hasBall(message.data) ? ballPathOf(message.data) : live.ballPath,
      };
    }
    case 'moment':
      return addMoment(live, message.data);
    case 'commentary':
      return withLine(live, message.data);
    case 'analysis':
      return { ...live, analysis: { ...live.analysis, [message.data.momentId]: message.data } };
    case 'milestone':
      return { ...live, milestones: upsert(live.milestones, message.data, (m) => m.id) };
    case 'momentum':
      return { ...live, momentum: upsert(live.momentum, message.data, (m) => `${m.period}:${m.fromMinute}`) };
    case 'overlay':
      return { ...live, overlays: upsert(live.overlays, message.data, (o) => o.id) };
    case 'shape':
      // A replay after a reconnect can resend an older shape, so the view never steps backwards.
      return live.shape && live.shape.sequence > message.data.sequence ? live : { ...live, shape: message.data };
    default:
      return live;
  }
}
