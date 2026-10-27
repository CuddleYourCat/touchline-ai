import { useCallback, useEffect, useReducer, useRef, useState } from 'react';
import { api } from '../api/client';
import { checkForMaintenance } from '../api/maintenance';
import { defaultViewer, viewerQuery, type Viewer } from '../viewer/viewer';
import {
  applyMessage,
  initialLiveMatch,
  mergeMoments,
  streamEventNames,
  type LiveMatch,
  type MatchDetail,
  type MomentView,
  type StreamMessage,
} from './matchStreamReducer';

/** How long the stream may stay silent (no message, no heartbeat) before the reconnecting banner shows. */
export const staleAfterMs = 30_000;

/** The first wait before reopening a stream the browser gave up on; it doubles on each failure. */
export const reconnectBaseMs = 1_000;

/** The longest wait between reopen attempts, before jitter. */
export const reconnectMaxMs = 15_000;

/** The most random delay added to each reopen, so many viewers don't all reconnect at once after a deploy. */
export const reconnectJitterMs = 250;

export type { Viewer } from '../viewer/viewer';

/** The live view's state: loading, a missing match, a failure, or the match itself (and whether the stream has gone quiet). */
export type MatchStreamState =
  | { kind: 'loading' }
  | { kind: 'notFound' }
  /** A fan match that couldn't be started, or that was never picked up (a 410). */
  | { kind: 'notStarted' }
  | { kind: 'error' }
  | { kind: 'ready'; match: LiveMatch; stale: boolean; momentsFailed: boolean };

type Action =
  | { type: 'reset' }
  | { type: 'notFound' }
  | { type: 'notStarted' }
  | { type: 'error' }
  | { type: 'loaded'; detail: MatchDetail; moments: MomentView[] | undefined; language: string }
  | { type: 'moments'; matchId: string; moments: MomentView[] | undefined }
  | { type: 'stale'; stale: boolean }
  | { type: 'message'; message: StreamMessage };

/**
 * A reload for a new viewer, on the same match: the moments and their commentary are the new viewer's, but everything
 * that doesn't depend on the viewer stays on screen. The reopened stream resumes from the new detail's
 * `lastSequence`, so it never resends the events, shape, momentum, milestones, overlays or analysis seen so far, and
 * dropping them would blank the pitch until the next event.
 */
function keepPicture(shown: LiveMatch, fresh: LiveMatch): LiveMatch {
  return {
    ...fresh,
    events: shown.events,
    shape: shown.shape,
    ballPath: shown.ballPath,
    momentum: shown.momentum,
    milestones: shown.milestones,
    overlays: shown.overlays,
    analysis: shown.analysis,
  };
}

function reducer(state: MatchStreamState, action: Action): MatchStreamState {
  switch (action.type) {
    case 'reset':
      return state.kind === 'loading' ? state : { kind: 'loading' };
    case 'notFound':
      return { kind: 'notFound' };
    case 'notStarted':
      return { kind: 'notStarted' };
    case 'error':
      return { kind: 'error' };
    case 'loaded': {
      const fresh = initialLiveMatch(action.detail, action.moments ?? [], action.language);
      const shown = state.kind === 'ready' && state.match.detail.summary.id === action.detail.summary.id ? state.match : undefined;
      return {
        kind: 'ready',
        match: shown ? keepPicture(shown, fresh) : fresh,
        stale: false,
        momentsFailed: action.moments === undefined,
      };
    }
    case 'moments':
      if (state.kind !== 'ready' || state.match.detail.summary.id !== action.matchId) return state;
      return action.moments
        ? { ...state, match: mergeMoments(state.match, action.moments), momentsFailed: false }
        : { ...state, momentsFailed: true };
    case 'stale':
      return state.kind === 'ready' && state.stale !== action.stale ? { ...state, stale: action.stale } : state;
    case 'message':
      return state.kind === 'ready' ? { ...state, match: applyMessage(state.match, action.message) } : state;
  }
}

type DetailResult = { kind: 'found'; detail: MatchDetail } | { kind: 'notFound' } | { kind: 'notStarted' } | { kind: 'error' };

/** Loads the detail. Server error text is deliberately dropped. */
async function fetchDetail(matchId: string): Promise<DetailResult> {
  try {
    const { data, response } = await api.GET('/api/matches/{matchId}', { params: { path: { matchId } } });
    if (data) return { kind: 'found', detail: data };
    if (response.status === 404) return { kind: 'notFound' };
    if (response.status === 410) return { kind: 'notStarted' };
    return { kind: 'error' };
  } catch {
    return { kind: 'error' };
  }
}

async function fetchMomentsOnce(matchId: string, viewer: Viewer): Promise<MomentView[] | undefined> {
  try {
    const { data } = await api.GET('/api/matches/{matchId}/moments', {
      params: { path: { matchId }, query: viewerQuery(viewer) },
    });
    return data ?? undefined;
  } catch {
    return undefined;
  }
}

/** Loads the moments so far, retrying once. `undefined` means both tries failed; server error text is dropped. */
async function fetchMoments(matchId: string, viewer: Viewer): Promise<MomentView[] | undefined> {
  return (await fetchMomentsOnce(matchId, viewer)) ?? (await fetchMomentsOnce(matchId, viewer));
}

/** The wait before reopen attempt `failures` (1-based): 1s, 2s, 4s and so on, capped, plus a little jitter. */
function backoffMs(failures: number): number {
  return Math.min(reconnectBaseMs * 2 ** (failures - 1), reconnectMaxMs) + Math.floor(Math.random() * reconnectJitterMs);
}

/**
 * The live match (ADR-035). Loads the detail, then the moments (so they're at least as new as the detail's
 * `lastSequence`), then opens the stream from that `lastSequence`. Anything the moments fetch saw beyond it arrives
 * again on the stream and is merged by ID, so nothing is missed. The browser's `EventSource` retries a dropped
 * connection with `Last-Event-ID` on its own; when it gives up for good (`readyState` CLOSED, for example after a 502
 * during a deploy) this hook reopens it with exponential backoff, resuming `after` the last `id` it saw. It also
 * watches for silence and marks the state `stale` after {@link staleAfterMs}, or straight away when the browser gives
 * up; the flag clears on the next message. The stream is closed, and never reopened, on `fulltime` and on unmount.
 * A match already at full time opens no stream.
 */
export function useMatchStream(
  matchId: string,
  viewer?: Viewer,
): { state: MatchStreamState; retry: () => void; retryMoments: () => void; viewer: Viewer } {
  const [state, dispatch] = useReducer(reducer, { kind: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const fallback = defaultViewer();
  const mode = viewer?.mode ?? fallback.mode;
  const language = viewer?.language ?? fallback.language;
  // A new match, or a retry, shows the loading state. A new viewer keeps the match on screen while it reloads.
  const shown = useRef<string | undefined>(undefined);

  useEffect(() => {
    const key = `${matchId}#${attempt}`;
    if (shown.current !== key) {
      shown.current = key;
      dispatch({ type: 'reset' });
    }
    const who: Viewer = { mode, language };
    let cancelled = false;
    let finished = false;
    let source: EventSource | undefined;
    let staleTimer: ReturnType<typeof setTimeout> | undefined;
    let reopenTimer: ReturnType<typeof setTimeout> | undefined;
    let failures = 0;
    let lastSequence = 0;

    const stopWatching = () => {
      if (staleTimer !== undefined) clearTimeout(staleTimer);
      staleTimer = undefined;
    };
    const watch = () => {
      stopWatching();
      staleTimer = setTimeout(() => dispatch({ type: 'stale', stale: true }), staleAfterMs);
    };
    const heard = () => {
      failures = 0;
      dispatch({ type: 'stale', stale: false });
      watch();
    };
    const close = () => {
      stopWatching();
      if (reopenTimer !== undefined) clearTimeout(reopenTimer);
      reopenTimer = undefined;
      source?.close();
      source = undefined;
    };

    const open = () => {
      const query = new URLSearchParams({ after: String(lastSequence), ...viewerQuery(who) });
      const es = new EventSource(`/api/matches/${encodeURIComponent(matchId)}/stream?${query.toString()}`);
      source = es;
      for (const name of streamEventNames) {
        es.addEventListener(name, (e: MessageEvent<string>) => {
          if (cancelled || finished || source !== es) return;
          heard();
          if (name === 'heartbeat') return;
          const id = Number(e.lastEventId);
          if (e.lastEventId && Number.isFinite(id)) lastSequence = id;
          let data: unknown;
          try {
            data = JSON.parse(e.data);
          } catch {
            return;
          }
          dispatch({ type: 'message', message: { type: name, data } as StreamMessage });
          if (name === 'fulltime') {
            finished = true;
            close();
            dispatch({ type: 'stale', stale: false });
          }
        });
      }
      es.addEventListener('error', () => {
        if (cancelled || finished || source !== es || es.readyState !== EventSource.CLOSED) return;
        close();
        dispatch({ type: 'stale', stale: true });
        // EventSource can't see the 503's headers, so ask whether it was maintenance (ADR-060).
        void checkForMaintenance();
        failures += 1;
        reopenTimer = setTimeout(() => {
          reopenTimer = undefined;
          if (!cancelled && !finished) open();
        }, backoffMs(failures));
      });
      watch();
    };

    void (async () => {
      const detail = await fetchDetail(matchId);
      if (cancelled) return;
      if (detail.kind !== 'found') {
        dispatch({ type: detail.kind });
        return;
      }
      const moments = await fetchMoments(matchId, who);
      if (cancelled) return;
      dispatch({ type: 'loaded', detail: detail.detail, moments, language: who.language });
      if (detail.detail.state.fullTime) return;
      lastSequence = detail.detail.state.lastSequence;
      open();
    })();

    return () => {
      cancelled = true;
      close();
    };
  }, [matchId, attempt, mode, language]);

  const retry = useCallback(() => setAttempt((n) => n + 1), []);

  const retryMoments = useCallback(() => {
    const who: Viewer = { mode, language };
    void fetchMoments(matchId, who).then((moments) => dispatch({ type: 'moments', matchId, moments }));
  }, [matchId, mode, language]);

  return { state, retry, retryMoments, viewer: { mode, language } };
}
