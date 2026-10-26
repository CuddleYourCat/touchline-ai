import { useCallback, useEffect, useRef, useState } from 'react';
import { defaultViewer, viewerForMatch, viewerQuery, type Viewer } from '../viewer/viewer';
import {
  applyMessage,
  initialLiveMatch,
  streamEventNames,
  type LiveMatch,
  type LiveState,
  type MatchEvent,
  type StreamMessage,
} from './matchStreamReducer';

/** Where a replay starts: just before an event, or just before the first event at or after a match time. */
export type ReplayTarget = { sequence: number } | { period: 'FirstHalf' | 'SecondHalf'; minute: number };

/** A replay of part of a match, playing beside the live stream (ADR-067). */
export interface MatchReplay {
  status: 'idle' | 'loading' | 'playing' | 'error';
  /** The replay's view of the match, built with the same reducer as the live view, or null when idle. */
  match: LiveMatch | null;
  /** The playhead, or null until the replay's first message has played. */
  clock: LiveState['clock'] | null;
  /** Match seconds behind the live clock, or null until the replay has a playhead. */
  behindLiveSeconds: number | null;
  /** Starts a replay, or restarts one from a new point. */
  start: (target: ReplayTarget) => void;
  /** Stops the replay and frees its stream. */
  backToLive: () => void;
}

/** How fast a replay plays when the caller doesn't say: the default match speed. */
export const defaultReplaySpeed = 10;

/** How many events before the chosen one a replay starts, so the build-up shows (the server's `BuildUp`). */
export const replayBuildUp = 2;

/** The half-time pause in match seconds (ADR-066), before dividing by the speed. */
const halfTimeMatchSeconds = 15 * 60;

/** The shortest half-time pause, so the card can be read at any speed (ADR-066). */
const minHalfTimeMs = 15_000;

/** The half-time pause at a speed: `max(15 min / speed, 15 s)`. */
export function halfTimePauseMs(speed: number): number {
  return Math.max((halfTimeMatchSeconds / speed) * 1000, minHalfTimeMs);
}

/** Seconds on a match clock reading. The second half counts on from 45:00, so the halves share one scale. */
function clockSeconds(clock: LiveState['clock']): number {
  return clock.minute * 60 + clock.second;
}

const timeSpan = /^(?:(\d+)\.)?(\d+):(\d+):(\d+(?:\.\d+)?)$/;

/** An event's match time in seconds, from its `time.clock` (a .NET TimeSpan such as `00:12:05.5000000`). */
export function eventSeconds(event: MatchEvent): number | null {
  const match = timeSpan.exec(event.time.clock);
  if (!match) return null;
  const [, days, hours, minutes, seconds] = match;
  return Number(days ?? 0) * 86_400 + Number(hours) * 3600 + Number(minutes) * 60 + Number(seconds);
}

/**
 * How far the replay is behind live, in match seconds. Within a half it's the clock difference. Across half time it's
 * the second half played so far plus the replay's distance to 45:00, so first-half added time still to come is not
 * counted, and the half-time pause never is.
 */
export function secondsBehind(live: LiveState['clock'], replay: LiveState['clock']): number {
  const half = 45 * 60;
  if (live.period === replay.period) return Math.max(0, clockSeconds(live) - clockSeconds(replay));
  if (live.period === 'SecondHalf') return Math.max(0, clockSeconds(live) - half) + Math.max(0, half - clockSeconds(replay));
  return 0;
}

/** One event and the messages that follow it (its state, shape, moment, commentary, overlay and so on). */
interface Frame {
  messages: StreamMessage[];
  event: MatchEvent | null;
}

type StateMessage = Extract<StreamMessage, { type: 'state' | 'fulltime' }>;

const stateOf = (frame: Frame): LiveState | null =>
  frame.messages.find((m): m is StateMessage => m.type === 'state' || m.type === 'fulltime')?.data ?? null;

/** The match time a frame plays at: its event's time, or its state's clock when the event's can't be read. */
function frameSeconds(frame: Frame): number | null {
  if (frame.event) {
    const fromEvent = eventSeconds(frame.event);
    if (fromEvent !== null) return fromEvent;
  }
  const state = stateOf(frame);
  return state ? clockSeconds(state.clock) : null;
}

const isFirstHalfEnd = (frame: Frame) => frame.event?.type === 'PeriodEnd' && frame.event.time.period === 'FirstHalf';

/**
 * The starting picture for a replay from just after `after`: the live match's detail, with the moments, their
 * commentary and analysis, the milestones and the momentum windows that came before that point. Events, overlays, the
 * shape and the ball start empty and fill as the replay plays. The state is the live one only until the replay's first
 * `state` message replaces it, in the same step, so the score shown is the score at the start point.
 */
function seed(live: LiveMatch, after: number, clock: LiveState['clock'] | null): LiveMatch {
  const fresh = initialLiveMatch(live.detail, [], live.language);
  const moments = Object.values(live.moments).filter((m) => m.sequence <= after);
  const ids = new Set(moments.map((m) => m.id));
  const before = (period: LiveState['clock']['period'], toMinute: number) =>
    clock !== null &&
    ((period === 'FirstHalf' && clock.period === 'SecondHalf') || (period === clock.period && toMinute <= clock.minute));
  return {
    ...fresh,
    fullTime: false,
    moments: Object.fromEntries(moments.map((m) => [m.id, m])),
    commentary: Object.fromEntries(Object.entries(live.commentary).filter(([id]) => ids.has(id))),
    analysis: Object.fromEntries(Object.entries(live.analysis).filter(([id]) => ids.has(id))),
    milestones: live.milestones.filter((m) => m.sequence <= after),
    momentum: live.momentum.filter((m) => before(m.period, m.toMinute)),
  };
}

type ReplayView = { matchId: string; status: MatchReplay['status']; match: LiveMatch | null };

const idle = (matchId: string): ReplayView => ({ matchId, status: 'idle', match: null });

/**
 * A replay from a point (ADR-067). `start` opens a second stream beside the live one, from {@link replayBuildUp}
 * events before the chosen event (`after = sequence - 2`, never below 0) or from a match time (`fromPeriod` and
 * `fromMinute`, which the server resolves the same way), with `replay=true` so every shape comes back and the players
 * move as they did. The messages are buffered and played on a client clock: each event plays its match-time gap after
 * the one before, divided by `speed` (the match speed; 10 by default), and the first half's `PeriodEnd` holds for
 * `max(15 min / speed, 15 s)` as at half time live (ADR-066). Every message goes through the live view's reducer, so
 * `match` has positions, ball, score, moments, overlays and shapes exactly as live had them.
 *
 * The replay ends on its own, back to `idle`, when its playhead reaches the live edge (the live match's last sequence)
 * or, on a finished match, when it plays full time. `backToLive`, a restart and unmounting all close its stream and
 * clear its timers first. A `start` before the live match has loaded does nothing.
 *
 * **Overlay and goal cards in a replay:** they arrive in `match.overlays` as usual, with the cards' own IDs, as the
 * playhead passes them. While `status` isn't `idle`, every card in `match.overlays` is a replayed one, so the page
 * shows the replay's `match` in place of the live one and passes `replay` to the overlay and goal toasts to mark them.
 *
 * `options.viewer` is the live stream's viewer (from `useMatchStream`), so the replay's commentary and cards are shaped
 * the same way.
 */
export function useMatchReplay(
  matchId: string,
  live: LiveMatch | null,
  options?: { speed?: number; viewer?: Viewer },
): MatchReplay {
  const speed = options?.speed && options.speed > 0 ? options.speed : defaultReplaySpeed;
  const fallback = defaultViewer();
  const mode = options?.viewer?.mode ?? fallback.mode;
  const language = options?.viewer?.language ?? fallback.language;
  const team = options?.viewer?.team;

  const [view, setView] = useState<ReplayView>(() => idle(matchId));
  const liveRef = useRef(live);
  const stopRef = useRef<(() => void) | null>(null);
  const settings = useRef({ speed, mode, language, team, matchId });

  useEffect(() => {
    liveRef.current = live;
    settings.current = { speed, mode, language, team, matchId };
  });

  // A new match, or unmounting, stops the replay and frees its stream.
  useEffect(
    () => () => {
      stopRef.current?.();
      stopRef.current = null;
    },
    [matchId],
  );

  const backToLive = useCallback(() => {
    stopRef.current?.();
    stopRef.current = null;
    setView(idle(settings.current.matchId));
  }, []);

  const start = useCallback(
    (target: ReplayTarget) => {
      stopRef.current?.();
      stopRef.current = null;
      const startedWith = liveRef.current;
      if (!startedWith) return;
      const { speed: pace, mode: who, language: lang, team: club, matchId: id } = settings.current;

      let source: EventSource | undefined;
      let timer: ReturnType<typeof setTimeout> | undefined;
      let stopped = false;
      let received = false;
      let match: LiveMatch | null = null;
      let playedAt = 0;
      let previous: Frame | null = null;
      const queue: Frame[] = [];

      const stop = () => {
        stopped = true;
        if (timer !== undefined) clearTimeout(timer);
        timer = undefined;
        source?.close();
        source = undefined;
      };
      stopRef.current = stop;
      const finish = () => {
        stop();
        if (stopRef.current === stop) stopRef.current = null;
        setView(idle(id));
      };

      const waitBefore = (next: Frame): number => {
        if (!previous) return 0;
        if (isFirstHalfEnd(previous)) return halfTimePauseMs(pace);
        const from = frameSeconds(previous);
        const to = frameSeconds(next);
        return from === null || to === null ? 0 : (Math.max(0, to - from) / pace) * 1000;
      };

      const play = () => {
        timer = undefined;
        if (stopped) return;
        const frame = queue.shift();
        if (!frame) return;
        if (!match) {
          const first = stateOf(frame);
          const after = frame.event ? frame.event.sequence - 1 : (first?.lastSequence ?? 0);
          match = seed(liveRef.current ?? startedWith, after, first?.clock ?? null);
        }
        previous = frame;
        playedAt = Date.now();
        if (!show(frame.messages.reduce(applyMessage, match))) schedule();
      };

      // Shows the replay's match, or ends the replay when it has reached the live edge or full time. True when ended.
      const show = (next: LiveMatch): boolean => {
        match = next;
        const now = liveRef.current;
        const caughtUp = now && !now.fullTime && next.state.lastSequence >= now.state.lastSequence;
        if (next.fullTime || caughtUp) {
          finish();
          return true;
        }
        setView({ matchId: id, status: 'playing', match: next });
        return false;
      };

      // Plays the next frame once its gap since the last one has passed, less any time already spent waiting for it.
      const schedule = () => {
        if (stopped || timer !== undefined) return;
        const next = queue[0];
        if (!next) return;
        // The first frame sets the starting picture, so it waits for its state (the score at the start point).
        if (!previous && !stateOf(next) && queue.length < 2 && source !== undefined) return;
        const wait = Math.max(0, waitBefore(next) - (previous ? Date.now() - playedAt : 0));
        timer = setTimeout(play, wait);
      };

      // Messages arrive in order, so one that isn't an event belongs to the last event received: still queued, or
      // already played, in which case it's applied straight away.
      const receive = (message: StreamMessage) => {
        const last = queue.at(-1);
        if (message.type === 'event') {
          queue.push({ messages: [message], event: message.data });
        } else if (last) {
          last.messages.push(message);
        } else if (match) {
          if (show(applyMessage(match, message))) return;
        } else {
          queue.push({ messages: [message], event: null });
        }
        // The stream ends after full time, so the replay has everything it needs.
        if (message.type === 'fulltime') {
          source?.close();
          source = undefined;
        }
        schedule();
      };

      const viewer = viewerForMatch(club ? { mode: who, language: lang, team: club } : { mode: who, language: lang }, [
        startedWith.detail.summary.home.id,
        startedWith.detail.summary.away.id,
      ]);
      const from: Record<string, string> =
        'sequence' in target
          ? { after: String(Math.max(0, target.sequence - replayBuildUp)) }
          : { fromPeriod: target.period, fromMinute: String(target.minute) };
      const query = new URLSearchParams({ ...from, replay: 'true', ...viewerQuery(viewer) });
      const es = new EventSource(`/api/matches/${encodeURIComponent(id)}/stream?${query.toString()}`);
      source = es;
      for (const name of streamEventNames) {
        if (name === 'heartbeat') continue;
        es.addEventListener(name, (e: MessageEvent<string>) => {
          if (stopped || source !== es) return;
          let data: unknown;
          try {
            data = JSON.parse(e.data);
          } catch {
            return;
          }
          received = true;
          receive({ type: name, data } as StreamMessage);
        });
      }
      es.addEventListener('error', () => {
        if (stopped || source !== es || es.readyState !== EventSource.CLOSED) return;
        stop();
        if (stopRef.current === stop) stopRef.current = null;
        setView({ matchId: id, status: 'error', match: received ? match : null });
      });
      setView({ matchId: id, status: 'loading', match: null });
    },
    [],
  );

  const current = view.matchId === matchId ? view : idle(matchId);
  const clock = current.match && current.status === 'playing' ? current.match.state.clock : null;
  return {
    status: current.status,
    match: current.match,
    clock,
    behindLiveSeconds: clock && live ? secondsBehind(live.state.clock, clock) : null,
    start,
    backToLive,
  };
}
