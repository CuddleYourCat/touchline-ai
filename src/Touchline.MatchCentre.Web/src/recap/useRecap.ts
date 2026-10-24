import { useCallback, useEffect, useState } from 'react';
import { api } from '../api/client';
import type { components } from '../api/schema';
import type { MatchDetail } from '../live/matchStreamReducer';
import { viewerForMatch, viewerQuery, type Viewer } from '../viewer/viewer';

/** The Producer's full-time recap. */
export type MatchRecap = components['schemas']['MatchRecap'];

/** How long to wait between checks for the model's recap while the template is shown. */
export const recapPollMs = 5_000;

/** How many times to check before giving up and keeping the template: about a minute in all. */
export const recapMaxPolls = 12;

/** The recap page's state. Server error text is never kept. */
export type RecapState =
  | { kind: 'loading' }
  | { kind: 'notFound' }
  | { kind: 'error' }
  | { kind: 'notFullTime'; detail: MatchDetail }
  | { kind: 'unavailable'; detail: MatchDetail }
  | {
      kind: 'ready';
      detail: MatchDetail;
      recap: MatchRecap;
      /** True when this recap stands in for a model recap that hasn't arrived (a template, on a non-demo match). */
      provisional: boolean;
      /** True while the page is still checking for the model's recap. */
      waiting: boolean;
    };

/**
 * True when a template recap is waiting on the model's. A demo match's recap is always the template and is final:
 * the Match Centre serves the demo squad's recap for `demo-*` matches and never stores a model one (RecapReader).
 */
export function awaitsModelRecap(recap: MatchRecap, matchId: string): boolean {
  return recap.source === 'Template' && !matchId.startsWith('demo-');
}

type RecapResult = { kind: 'recap'; recap: MatchRecap } | { kind: 'notFound' } | { kind: 'notFullTime' } | { kind: 'unavailable' };

async function fetchRecap(matchId: string, viewer: Viewer): Promise<RecapResult> {
  try {
    const { data, response } = await api.GET('/api/matches/{matchId}/recap', {
      params: { path: { matchId }, query: viewerQuery(viewer) },
    });
    if (data) return { kind: 'recap', recap: data };
    if (response.status === 404) return { kind: 'notFound' };
    if (response.status === 409) return { kind: 'notFullTime' };
    return { kind: 'unavailable' };
  } catch {
    return { kind: 'unavailable' };
  }
}

async function fetchDetail(matchId: string): Promise<MatchDetail | 'notFound' | 'error'> {
  try {
    const { data, response } = await api.GET('/api/matches/{matchId}', { params: { path: { matchId } } });
    if (data) return data;
    return response.status === 404 ? 'notFound' : 'error';
  } catch {
    return 'error';
  }
}

/**
 * Loads a finished match's recap for the viewer. The Event Processor writes the model's recap at full time (ADR-030),
 * so for a short while the Match Centre serves the template instead. While it does, this checks again every
 * {@link recapPollMs}, at most {@link recapMaxPolls} times, and stops on unmount or a change of viewer.
 */
export function useRecap(matchId: string, viewer: Viewer): { state: RecapState; retry: () => void } {
  const [state, setState] = useState<RecapState>({ kind: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const { mode, language, team } = viewer;

  useEffect(() => {
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;

    void (async () => {
      const detail = await fetchDetail(matchId);
      if (cancelled) return;
      if (detail === 'notFound' || detail === 'error') {
        setState({ kind: detail });
        return;
      }
      const who = viewerForMatch(team ? { mode, language, team } : { mode, language }, [detail.summary.home.id, detail.summary.away.id]);

      const check = async (polls: number) => {
        const result = await fetchRecap(matchId, who);
        if (cancelled) return;
        switch (result.kind) {
          case 'notFound':
            setState({ kind: 'notFound' });
            return;
          case 'notFullTime':
            setState({ kind: 'notFullTime', detail });
            return;
          case 'unavailable':
            // A failed re-check keeps the template on screen; only a first failure is shown.
            setState((s) => (s.kind === 'ready' ? { ...s, waiting: false } : { kind: 'unavailable', detail }));
            return;
          case 'recap': {
            const provisional = awaitsModelRecap(result.recap, matchId);
            const waiting = provisional && polls < recapMaxPolls;
            setState({ kind: 'ready', detail, recap: result.recap, provisional, waiting });
            if (waiting) timer = setTimeout(() => void check(polls + 1), recapPollMs);
          }
        }
      };
      await check(0);
    })();

    return () => {
      cancelled = true;
      if (timer !== undefined) clearTimeout(timer);
    };
  }, [matchId, mode, language, team, attempt]);

  // A retry shows the loading state, unless a recap is already on screen (it stays while the page checks again).
  const retry = useCallback(() => {
    setState((s) => (s.kind === 'ready' ? s : { kind: 'loading' }));
    setAttempt((n) => n + 1);
  }, []);
  return { state, retry };
}
