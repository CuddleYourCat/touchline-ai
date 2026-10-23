import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from '../api/client';
import type { components } from '../api/schema';

/** One match as the list endpoint returns it. */
export type MatchSummary = components['schemas']['MatchSummary'];

/** How often the list refreshes while the page is visible, so live scores move. */
export const refreshIntervalMs = 15_000;

/** The list's state: loading first time, then either the matches or a failure. */
export type MatchesState =
  | { kind: 'loading' }
  | { kind: 'error' }
  | { kind: 'ready'; matches: MatchSummary[] };

/** Fetches the list; `undefined` means it failed. Server error text is deliberately dropped. */
async function fetchMatches(): Promise<MatchSummary[] | undefined> {
  try {
    const { data } = await api.GET('/api/matches');
    return data;
  } catch {
    return undefined;
  }
}

/** A failed refresh keeps the last good list; only a failure with nothing to show is an error. */
function nextState(previous: MatchesState, matches: MatchSummary[] | undefined): MatchesState {
  if (matches) return { kind: 'ready', matches };
  return previous.kind === 'ready' ? previous : { kind: 'error' };
}

/**
 * Loads `GET /api/matches` and refreshes it every 15 seconds while the tab is visible.
 * Polling stops when the tab is hidden and resumes, with an immediate refresh, when it comes back.
 * Each request is numbered, and a response older than the newest one applied is ignored, so a slow
 * refresh can't overwrite a newer list.
 */
export function useMatches(): { state: MatchesState; retry: () => void } {
  const [state, setState] = useState<MatchesState>({ kind: 'loading' });
  const lastRequested = useRef(0);
  const lastApplied = useRef(0);

  const refresh = useCallback(() => {
    const request = ++lastRequested.current;
    void fetchMatches().then((matches) => {
      if (request < lastApplied.current) return;
      lastApplied.current = request;
      setState((previous) => nextState(previous, matches));
    });
  }, []);

  useEffect(() => {
    let timer: ReturnType<typeof setInterval> | undefined;

    const start = () => {
      if (timer === undefined) timer = setInterval(refresh, refreshIntervalMs);
    };
    const stop = () => {
      if (timer !== undefined) clearInterval(timer);
      timer = undefined;
    };
    const onVisibilityChange = () => {
      if (document.hidden) {
        stop();
      } else {
        refresh();
        start();
      }
    };

    refresh();
    if (!document.hidden) start();
    document.addEventListener('visibilitychange', onVisibilityChange);
    return () => {
      stop();
      document.removeEventListener('visibilitychange', onVisibilityChange);
    };
  }, [refresh]);

  const retry = useCallback(() => {
    setState({ kind: 'loading' });
    refresh();
  }, [refresh]);

  return { state, retry };
}
