import { api } from '../api/client';
import type { components } from '../api/schema';
import { fieldErrors, retryAfterSeconds, type FieldErrors } from '../api/problems';

/** A fan's request to play a match (ADR-032). */
export type MatchRequest = components['schemas']['MatchRequest'];

/**
 * What starting a match came to. `pipelineOff` (a 503) isn't a failure: the request and sheets were valid, but fan
 * matches only run when the match pipeline is switched on.
 */
export type StartOutcome =
  | { kind: 'accepted'; matchId: string }
  | { kind: 'pipelineOff' }
  | { kind: 'busy'; retryAfterSeconds: number | null }
  | { kind: 'sameTeam' }
  | { kind: 'unknownTeam' }
  | { kind: 'invalid'; errors: FieldErrors }
  | { kind: 'error' };

/**
 * The outcome for a response. Pure, so every status is tested without a network. Server text is never kept: only
 * the status, `Retry-After`, the match ID and a 400's field messages.
 */
export function outcomeFor(status: number, body: unknown, retryAfter: string | null, now: number = Date.now()): StartOutcome {
  switch (status) {
    case 202: {
      const matchId = typeof body === 'object' && body !== null ? (body as { matchId?: unknown }).matchId : undefined;
      return typeof matchId === 'string' && matchId.length > 0 ? { kind: 'accepted', matchId } : { kind: 'error' };
    }
    case 400:
      return { kind: 'invalid', errors: fieldErrors(body) };
    case 404:
      return { kind: 'unknownTeam' };
    case 409:
      return { kind: 'sameTeam' };
    case 429:
      return { kind: 'busy', retryAfterSeconds: retryAfterSeconds(retryAfter, now) };
    case 503:
      return { kind: 'pipelineOff' };
    default:
      return { kind: 'error' };
  }
}

/**
 * Starts an exhibition. The idempotency key makes a repeated submit (a double click, a retry after a dropped
 * response) return the same match instead of starting two.
 */
export async function startMatch(request: MatchRequest, idempotencyKey: string): Promise<StartOutcome> {
  try {
    const { data, error, response } = await api.POST('/api/matches', {
      params: { header: { 'Idempotency-Key': idempotencyKey } },
      body: request,
    });
    return outcomeFor(response.status, data ?? error, response.headers.get('Retry-After'));
  } catch {
    return { kind: 'error' };
  }
}

/** Which side of the form a 400's field path belongs to. */
export function sideOfPath(path: string): 'home' | 'away' | 'general' {
  if (path === 'home' || path.startsWith('home.') || path === 'homeTeamId') return 'home';
  if (path === 'away' || path.startsWith('away.') || path === 'awayTeamId') return 'away';
  return 'general';
}

/** A 400's messages split by side. */
export function errorsBySide(errors: FieldErrors): Record<'home' | 'away' | 'general', string[]> {
  const split: Record<'home' | 'away' | 'general', string[]> = { home: [], away: [], general: [] };
  for (const [path, messages] of Object.entries(errors)) split[sideOfPath(path)].push(...messages);
  return split;
}
