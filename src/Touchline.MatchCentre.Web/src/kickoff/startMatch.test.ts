import { afterEach, describe, expect, it, vi } from 'vitest';
import { api } from '../api/client';
import { describeWait, fieldErrors, retryAfterSeconds } from '../api/problems';
import { errorsBySide, outcomeFor, startMatch } from './startMatch';

describe('outcomeFor', () => {
  it('maps each status to an outcome, keeping no server text', () => {
    expect(outcomeFor(202, { matchId: 'fan-m1', status: 'Scheduled', streamUrl: '/s' }, null)).toEqual({ kind: 'accepted', matchId: 'fan-m1' });
    expect(outcomeFor(202, {}, null)).toEqual({ kind: 'error' });
    expect(outcomeFor(404, { detail: 'secret' }, null)).toEqual({ kind: 'unknownTeam' });
    expect(outcomeFor(409, { detail: 'secret' }, null)).toEqual({ kind: 'sameTeam' });
    expect(outcomeFor(503, { detail: 'secret' }, null)).toEqual({ kind: 'pipelineOff' });
    expect(outcomeFor(500, { detail: 'secret' }, null)).toEqual({ kind: 'error' });
  });

  it('reads Retry-After as seconds or as a date', () => {
    const now = Date.parse('2026-10-24T12:00:00Z');
    expect(outcomeFor(429, {}, '120', now)).toEqual({ kind: 'busy', retryAfterSeconds: 120 });
    expect(outcomeFor(429, {}, 'Sat, 24 Oct 2026 12:01:30 GMT', now)).toEqual({ kind: 'busy', retryAfterSeconds: 90 });
    expect(outcomeFor(429, {}, null, now)).toEqual({ kind: 'busy', retryAfterSeconds: null });
  });

  it('keeps only the field messages of a 400, split by side', () => {
    const outcome = outcomeFor(
      400,
      { title: 'One or more validation errors occurred.', detail: 'x', errors: { home: ['Exactly one goalkeeper must start.'], awayTeamId: ['The away team is required.'], seed: [7] } },
      null,
    );

    expect(outcome).toEqual({
      kind: 'invalid',
      errors: { home: ['Exactly one goalkeeper must start.'], awayTeamId: ['The away team is required.'] },
    });
    expect(errorsBySide(outcome.kind === 'invalid' ? outcome.errors : {})).toEqual({
      home: ['Exactly one goalkeeper must start.'],
      away: ['The away team is required.'],
      general: [],
    });
  });
});

describe('startMatch', () => {
  afterEach(() => vi.restoreAllMocks());

  it('sends the idempotency key in the header', async () => {
    const post = vi.spyOn(api, 'POST').mockResolvedValue({
      data: { matchId: 'fan-m1', status: 'Scheduled', streamUrl: '/api/matches/fan-m1/stream' },
      error: undefined,
      response: new Response(null, { status: 202 }),
    } as never);

    const outcome = await startMatch({ homeTeamId: 'a', awayTeamId: 'b', home: null, away: null }, 'key-1');

    expect(outcome).toEqual({ kind: 'accepted', matchId: 'fan-m1' });
    expect(post).toHaveBeenCalledWith('/api/matches', {
      params: { header: { 'Idempotency-Key': 'key-1' } },
      body: { homeTeamId: 'a', awayTeamId: 'b', home: null, away: null },
    });
  });

  it('degrades to an error when the network fails', async () => {
    vi.spyOn(api, 'POST').mockRejectedValue(new TypeError('offline'));
    expect(await startMatch({ homeTeamId: 'a', awayTeamId: 'b' }, 'k')).toEqual({ kind: 'error' });
  });
});

describe('problems', () => {
  it('drops malformed error bodies', () => {
    expect(fieldErrors(null)).toEqual({});
    expect(fieldErrors({ errors: ['x'] })).toEqual({});
    expect(fieldErrors({ errors: { name: ['   ', 'Too long.'] } })).toEqual({ name: ['Too long.'] });
  });

  it('describes waits and ignores unreadable Retry-After values', () => {
    expect(retryAfterSeconds('soon')).toBeNull();
    expect(describeWait(1)).toBe('about 1 second');
    expect(describeWait(61)).toBe('about 2 minutes');
  });
});
