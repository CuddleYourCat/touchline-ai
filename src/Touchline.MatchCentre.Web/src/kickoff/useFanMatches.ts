import { useEffect, useState } from 'react';
import { api } from '../api/client';

/**
 * Whether a fan match can be started (ADR-072). `unknown` means the status call failed, and the page then shows the
 * form: the 503 handling on submit is the safety net.
 */
export type FanMatchesState = 'loading' | 'open' | 'comingSoon' | 'unknown';

/** Asks `/api/status` once whether fan matches can be started, before the fan picks any teams. */
export function useFanMatches(): FanMatchesState {
  const [state, setState] = useState<FanMatchesState>('loading');

  useEffect(() => {
    let cancelled = false;
    api
      .GET('/api/status')
      .then(({ data }) => {
        if (cancelled) return;
        setState(typeof data?.fanMatches !== 'boolean' ? 'unknown' : data.fanMatches ? 'open' : 'comingSoon');
      })
      .catch(() => {
        if (!cancelled) setState('unknown');
      });
    return () => {
      cancelled = true;
    };
  }, []);

  return state;
}
