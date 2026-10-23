import { useCallback, useEffect, useReducer, useState } from 'react';
import { api } from '../api/client';
import {
  applyMessage,
  initialLiveMatch,
  mergeMoments,
  streamEventNames,
  type CommentaryLine,
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

/** Who is watching: the viewing mode and the language (a BCP 47 tag such as `es-ES`). */
export interface Viewer {
  mode: 'Commentator' | 'Analyst';
  language: string;
}

/** The contract's name for a viewer mode, as sent in `mode=` and as it appears on a {@link CommentaryLine}. */
export function lineModeFor(mode: Viewer['mode']): CommentaryLine['mode'] {
  return mode === 'Analyst' ? 'Analyst' : 'Casual';
}

function defaultViewer(): Viewer {
  const language = typeof navigator !== 'undefined' && navigator.language ? navigator.language : 'en';
  return { mode: 'Commentator', language };
}

/** The live view's state: loading, a missing match, a failure, or the match itself (and whether the stream has gone quiet). */
export type MatchStreamState =
  | { kind: 'loading' }
  | { kind: 'notFound' }
  | { kind: 'error' }
  | { kind: 'ready'; match: LiveMatch; stale: boolean; momentsFailed: boolean };

type Action =
  | { type: 'reset' }
  | { type: 'notFound' }
  | { type: 'error' }
  | { type: 'loaded'; detail: MatchDetail; moments: MomentView[] | undefined; language: string }
  | { type: 'moments'; matchId: string; moments: MomentView[] | undefined }
  | { type: 'stale'; stale: boolean }
  | { type: 'message'; message: StreamMessage };

function reducer(state: MatchStreamState, action: Action): MatchStreamState {
  switch (action.type) {
    case 'reset':
      return state.kind === 'loading' ? state : { kind: 'loading' };
    case 'notFound':
      return { kind: 'notFound' };
    case 'error':
      return { kind: 'error' };
    case 'loaded':
      return {
        kind: 'ready',
        match: initialLiveMatch(action.detail, action.moments ?? [], action.language),
        stale: false,
        momentsFailed: action.moments === undefined,
      };
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

type DetailResult = { kind: 'found'; detail: MatchDetail } | { kind: 'notFound' } | { kind: 'error' };

/** Loads the detail. Server error text is deliberately dropped. */
async function fetchDetail(matchId: string): Promise<DetailResult> {
  try {
    const { data, response } = await api.GET('/api/matches/{matchId}', { params: { path: { matchId } } });
    if (data) return { kind: 'found', detail: data };
    return response.status === 404 ? { kind: 'notFound' } : { kind: 'error' };
  } catch {
    return { kind: 'error' };
  }
}

async function fetchMomentsOnce(matchId: string, viewer: Viewer): Promise<MomentView[] | undefined> {
  try {
    const { data } = await api.GET('/api/matches/{matchId}/moments', {
      params: { path: { matchId }, query: { mode: lineModeFor(viewer.mode), lang: viewer.language } },
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

  useEffect(() => {
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
      const query = new URLSearchParams({ after: String(lastSequence), mode: lineModeFor(who.mode), lang: who.language });
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
      dispatch({ type: 'reset' });
    };
  }, [matchId, attempt, mode, language]);

  const retry = useCallback(() => setAttempt((n) => n + 1), []);

  const retryMoments = useCallback(() => {
    void fetchMoments(matchId, { mode, language }).then((moments) => dispatch({ type: 'moments', matchId, moments }));
  }, [matchId, mode, language]);

  return { state, retry, retryMoments, viewer: { mode, language } };
}
