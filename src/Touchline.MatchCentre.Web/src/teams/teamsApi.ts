import { api } from '../api/client';
import { fieldErrors, problemType, retryAfterSeconds, type FieldErrors } from '../api/problems';
import type { NameToCheck, NameVerdict, TeamBuilderOptions, TeamDetail, TeamDraft, TeamSummary } from './teamDraft';

/*
 * The team builder's calls (ADR-031), each turned into an outcome union. Server error text is never kept: only
 * statuses, the 403 problem types below (matched, not shown) and the field messages of a 400.
 */

/** The 403 problem type for a built-in team (`ProblemTypes.ReadOnlyTeam`). */
export const readOnlyType = 'https://touchline.example/problems/read-only-team';

/** The 403 problem type for a missing or wrong edit key (`ProblemTypes.WrongEditKey`). */
export const wrongKeyType = 'https://touchline.example/problems/wrong-edit-key';

/** The 503 problem type when names can't be checked, so nothing was saved (`ProblemTypes.NameCheckUnavailable`). */
export const namesUnavailableType = 'https://touchline.example/problems/name-check-unavailable';

/** Whether a response is the 503 for names that couldn't be checked, which means nothing was saved. */
function namesUnavailable(status: number, body: unknown): boolean {
  return status === 503 && problemType(body) === namesUnavailableType;
}

/** Every club; undefined when the call failed. */
export async function fetchTeams(): Promise<TeamSummary[] | undefined> {
  try {
    const { data } = await api.GET('/api/teams');
    return data;
  } catch {
    return undefined;
  }
}

/** A club in full. */
export type TeamLoad = { kind: 'ok'; detail: TeamDetail } | { kind: 'notFound' } | { kind: 'error' };

/** Loads one club. */
export async function fetchTeam(teamId: string): Promise<TeamLoad> {
  try {
    const { data, response } = await api.GET('/api/teams/{teamId}', { params: { path: { teamId } } });
    if (data) return { kind: 'ok', detail: data };
    return response.status === 404 ? { kind: 'notFound' } : { kind: 'error' };
  } catch {
    return { kind: 'error' };
  }
}

/** The builder's choices and rules; undefined when the call failed. */
export async function fetchOptions(): Promise<TeamBuilderOptions | undefined> {
  try {
    const { data } = await api.GET('/api/team-builder/options');
    return data;
  } catch {
    return undefined;
  }
}

/** The kinds the suggester takes (not abbreviations). */
export type SuggestKind = 'team' | 'venue' | 'manager' | 'player';

/** Suggested fictional names; undefined when the call failed. */
export async function suggestNames(kind: SuggestKind, count: number): Promise<string[] | undefined> {
  try {
    const { data } = await api.GET('/api/team-builder/names', { params: { query: { kind, count } } });
    return data?.names;
  } catch {
    return undefined;
  }
}

/** A name check's outcome. */
export type NameCheckOutcome = { kind: 'ok'; verdicts: NameVerdict[] } | { kind: 'rateLimited'; retryAfterSeconds: number | null } | { kind: 'namesUnavailable' } | { kind: 'error' };

/** Checks names with the guard (rate limited: 10 a minute per client). */
export async function checkNames(names: NameToCheck[]): Promise<NameCheckOutcome> {
  try {
    const { data, error, response } = await api.POST('/api/team-builder/names/check', { body: { names } });
    if (data) return { kind: 'ok', verdicts: data.names };
    if (namesUnavailable(response.status, error)) return { kind: 'namesUnavailable' };
    if (response.status === 429) return { kind: 'rateLimited', retryAfterSeconds: retryAfterSeconds(response.headers.get('Retry-After')) };
    return { kind: 'error' };
  } catch {
    return { kind: 'error' };
  }
}

/** Creating a team. The edit key is in `created` only, and is shown once. */
export type CreateOutcome =
  | { kind: 'created'; detail: TeamDetail; editKey: string }
  | { kind: 'invalid'; errors: FieldErrors }
  | { kind: 'rateLimited'; retryAfterSeconds: number | null }
  | { kind: 'namesUnavailable' }
  | { kind: 'error' };

/** Creates a fan team. */
export async function createTeam(draft: TeamDraft): Promise<CreateOutcome> {
  try {
    const { data, error, response } = await api.POST('/api/teams', { body: draft });
    if (data) return { kind: 'created', detail: data.team, editKey: data.editKey };
    if (response.status === 400) return { kind: 'invalid', errors: fieldErrors(error) };
    if (namesUnavailable(response.status, error)) return { kind: 'namesUnavailable' };
    if (response.status === 429) return { kind: 'rateLimited', retryAfterSeconds: retryAfterSeconds(response.headers.get('Retry-After')) };
    return { kind: 'error' };
  } catch {
    return { kind: 'error' };
  }
}

/**
 * Why a write was refused with a 403. Both known problem types are matched explicitly. Any other 403 is `forbidden`, which
 * says nothing about the key, so the caller must keep it.
 */
export type Forbidden = { kind: 'readOnly' } | { kind: 'wrongKey' } | { kind: 'forbidden' };

function forbidden(body: unknown): Forbidden {
  switch (problemType(body)) {
    case readOnlyType:
      return { kind: 'readOnly' };
    case wrongKeyType:
      return { kind: 'wrongKey' };
    default:
      return { kind: 'forbidden' };
  }
}

/** Saving a team. */
export type SaveOutcome =
  | { kind: 'saved'; detail: TeamDetail }
  | { kind: 'invalid'; errors: FieldErrors }
  | { kind: 'stale' }
  | { kind: 'namesUnavailable' }
  | Forbidden
  | { kind: 'notFound' }
  | { kind: 'error' };

/** The `If-Match` value for a version: a strong entity tag, as the service sends it in `ETag`. */
export function ifMatchFor(version: number): string {
  return `"${version}"`;
}

/** Replaces a fan team. The key goes in the header, and the version the form was loaded at in `If-Match`. */
export async function replaceTeam(teamId: string, draft: TeamDraft, editKey: string, version: number): Promise<SaveOutcome> {
  try {
    const { data, error, response } = await api.PUT('/api/teams/{teamId}', {
      params: { path: { teamId }, header: { 'Touchline-Edit-Key': editKey, 'If-Match': ifMatchFor(version) } },
      body: draft,
    });
    if (data) return { kind: 'saved', detail: data };
    switch (response.status) {
      case 400:
        return { kind: 'invalid', errors: fieldErrors(error) };
      case 403:
        return forbidden(error);
      case 404:
        return { kind: 'notFound' };
      case 503:
        return namesUnavailable(response.status, error) ? { kind: 'namesUnavailable' } : { kind: 'error' };
      case 412:
        return { kind: 'stale' };
      default:
        return { kind: 'error' };
    }
  } catch {
    return { kind: 'error' };
  }
}

/** Deleting a team. */
export type DeleteOutcome = { kind: 'deleted' } | { kind: 'playing' } | Forbidden | { kind: 'notFound' } | { kind: 'error' };

/** Deletes a fan team (soft, on the server). */
export async function deleteTeam(teamId: string, editKey: string): Promise<DeleteOutcome> {
  try {
    const { error, response } = await api.DELETE('/api/teams/{teamId}', {
      params: { path: { teamId }, header: { 'Touchline-Edit-Key': editKey } },
    });
    if (response.ok) return { kind: 'deleted' };
    switch (response.status) {
      case 403:
        return forbidden(error);
      case 404:
        return { kind: 'notFound' };
      case 409:
        return { kind: 'playing' };
      default:
        return { kind: 'error' };
    }
  } catch {
    return { kind: 'error' };
  }
}
