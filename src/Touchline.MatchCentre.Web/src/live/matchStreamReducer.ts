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
  fullTime: boolean;
  /** The viewer's language (a BCP 47 tag such as `es-ES`), which ranks competing lines. */
  language: string;
}

/** The starting point once the detail and the moments so far have loaded. */
export function initialLiveMatch(detail: MatchDetail, moments: MomentView[], language = 'en'): LiveMatch {
  const live: LiveMatch = {
    detail,
    state: detail.state,
    events: [],
    moments: {},
    commentary: {},
    analysis: {},
    milestones: [],
    momentum: [],
    overlays: [],
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
      return { ...live, events: events.length > maxEvents ? events.slice(-maxEvents) : events };
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
    default:
      return live;
  }
}
