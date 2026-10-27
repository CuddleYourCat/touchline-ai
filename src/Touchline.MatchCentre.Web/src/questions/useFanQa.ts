import { useEffect, useState } from 'react';
import { api } from '../api/client';

/**
 * Whether fans can ask the Analyst (ADR-072): `off` only when `/api/status` says the FanQa flag is off. Until it
 * answers, or if it can't, Ask the Analyst stays: the 503 on asking is the safety net.
 */
export type FanQaState = 'loading' | 'on' | 'off' | 'unknown';

/** Asks `/api/status` once whether fan questions are switched on. */
export function useFanQa(): FanQaState {
  const [state, setState] = useState<FanQaState>('loading');

  useEffect(() => {
    let cancelled = false;
    api
      .GET('/api/status')
      .then(({ data }) => {
        if (cancelled) return;
        setState(typeof data?.fanQa !== 'boolean' ? 'unknown' : data.fanQa ? 'on' : 'off');
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
