import { useCallback, useEffect, useState } from 'react';
import { forgetEditKey } from './editKeys';
import type { TeamBuilderOptions, TeamDetail, TeamSummary } from './teamDraft';
import { fetchOptions, fetchTeam, fetchTeams } from './teamsApi';

/** The club list's state. */
export type TeamsState = { kind: 'loading' } | { kind: 'error' } | { kind: 'ready'; teams: TeamSummary[] };

/** Loads every club once, with a retry. */
export function useTeams(): { state: TeamsState; retry: () => void } {
  const [state, setState] = useState<TeamsState>({ kind: 'loading' });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let cancelled = false;
    void fetchTeams().then((teams) => {
      if (!cancelled) setState(teams ? { kind: 'ready', teams } : { kind: 'error' });
    });
    return () => {
      cancelled = true;
    };
  }, [attempt]);

  const retry = useCallback(() => {
    setState({ kind: 'loading' });
    setAttempt((n) => n + 1);
  }, []);
  return { state, retry };
}

/** The builder's state: its options and, when editing, the team. `load` changes on every successful load. */
export type BuilderState =
  | { kind: 'loading' }
  | { kind: 'error' }
  | { kind: 'notFound' }
  | { kind: 'ready'; options: TeamBuilderOptions; detail: TeamDetail | null; load: number };

/**
 * Loads the builder's options and, for an existing team, the team, in parallel. A team that 404s has its stored
 * edit key pruned (ADR-053): it's gone, so the key is no use.
 */
export function useTeamBuilder(teamId: string | undefined): { state: BuilderState; reload: () => void } {
  const [state, setState] = useState<BuilderState>({ kind: 'loading' });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      const [options, team] = await Promise.all([fetchOptions(), teamId ? fetchTeam(teamId) : Promise.resolve(null)]);
      if (cancelled) return;
      if (team?.kind === 'notFound') {
        if (teamId) forgetEditKey(teamId);
        setState({ kind: 'notFound' });
      } else if (!options || team?.kind === 'error') {
        setState({ kind: 'error' });
      } else {
        setState({ kind: 'ready', options, detail: team ? team.detail : null, load: attempt });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [teamId, attempt]);

  const reload = useCallback(() => {
    setState({ kind: 'loading' });
    setAttempt((n) => n + 1);
  }, []);
  return { state, reload };
}

/** One club's detail for the kick-off sheet. `idle` until a team is chosen. */
export type DetailState =
  | { kind: 'idle' }
  | { kind: 'loading' }
  | { kind: 'error' }
  | { kind: 'notFound' }
  | { kind: 'ready'; detail: TeamDetail };

/**
 * Loads a club's detail whenever the chosen team changes. The result is stored with the ID it's for, and a result
 * for another ID reads as `loading`, so switching teams never shows the old squad and never sets state
 * synchronously inside the effect.
 */
export function useTeamDetail(teamId: string | undefined): { state: DetailState; retry: () => void } {
  const [result, setResult] = useState<{ forId: string; attempt: number; state: DetailState } | undefined>();
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!teamId) return;
    let cancelled = false;
    void fetchTeam(teamId).then((load) => {
      if (cancelled) return;
      const state: DetailState = load.kind === 'ok' ? { kind: 'ready', detail: load.detail } : { kind: load.kind };
      setResult({ forId: teamId, attempt, state });
    });
    return () => {
      cancelled = true;
    };
  }, [teamId, attempt]);

  const retry = useCallback(() => setAttempt((n) => n + 1), []);
  const state: DetailState = !teamId
    ? { kind: 'idle' }
    : result && result.forId === teamId && result.attempt === attempt
      ? result.state
      : { kind: 'loading' };
  return { state, retry };
}
