import { api } from '../api/client';
import { fieldErrors, problemTitle, retryAfterSeconds, type FieldErrors } from '../api/problems';
import type { NameToCheck, NameVerdict, TeamBuilderOptions, TeamDetail, TeamDraft, TeamSummary } from './teamDraft';

/*
 * The team builder's calls (ADR-031), each turned into an outcome union. Server error text is never kept: only
 * statuses, the 403 titles below (matched, not shown) and the field messages of a 400.
 */

/** The 403 title for a built-in team. */
export const readOnlyTitle = 'Read-only team';

/** The 403 title for a missing or wrong edit key (TeamBuilderEndpoints.Forbidden). */
export const wrongKeyTitle = 'Wrong edit key';

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
export type NameCheckOutcome = { kind: 'ok'; verdicts: NameVerdict[] } | { kind: 'rateLimited'; retryAfterSeconds: number | null } | { kind: 'error' };

/** Checks names with the guard (rate limited: 10 a minute per client). */
export async function checkNames(names: NameToCheck[]): Promise<NameCheckOutcome> {
  try {
    const { data, response } = await api.POST('/api/team-builder/names/check', { body: { names } });
    if (data) return { kind: 'ok', verdicts: data.names };
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
  | { kind: 'error' };

/** Creates a fan team. */
export async function createTeam(draft: TeamDraft): Promise<CreateOutcome> {
  try {
    const { data, error, response } = await api.POST('/api/teams', { body: draft });
    if (data) return { kind: 'created', detail: data.team, editKey: data.editKey };
    if (response.status === 400) return { kind: 'invalid', errors: fieldErrors(error) };
    if (response.status === 429) return { kind: 'rateLimited', retryAfterSeconds: retryAfterSeconds(response.headers.get('Retry-After')) };
    return { kind: 'error' };
  } catch {
    return { kind: 'error' };
  }
}

/**
 * Why a write was refused with a 403. Both known titles are matched explicitly. Any other 403 is `forbidden`, which
 * says nothing about the key, so the caller must keep it.
 */
export type Forbidden = { kind: 'readOnly' } | { kind: 'wrongKey' } | { kind: 'forbidden' };

function forbidden(body: unknown): Forbidden {
  switch (problemTitle(body)) {
    case readOnlyTitle:
      return { kind: 'readOnly' };
    case wrongKeyTitle:
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
