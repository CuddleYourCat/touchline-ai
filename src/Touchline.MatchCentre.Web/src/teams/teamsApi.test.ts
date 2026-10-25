import { afterEach, describe, expect, it, vi } from 'vitest';
import { api } from '../api/client';
import { detail, fail } from '../test/teamFixtures';
import { draftFromForm, formFromDetail } from './teamDraft';
import { checkNames, createTeam, deleteTeam, replaceTeam } from './teamsApi';

const draft = draftFromForm(formFromDetail(detail()));

describe('403s from the team calls', () => {
  afterEach(() => vi.restoreAllMocks());

  it.each([
    [{ type: 'https://touchline.example/problems/read-only-team', title: 'Anything' }, 'readOnly'],
    [{ type: 'https://touchline.example/problems/wrong-edit-key', title: 'Anything' }, 'wrongKey'],
    [{ title: 'Read-only team' }, 'forbidden'],
    [{ type: 'https://touchline.example/problems/something-new', title: 'Forbidden' }, 'forbidden'],
    [undefined, 'forbidden'],
  ])('maps a PUT 403 with %o to %s', async (body, kind) => {
    vi.spyOn(api, 'PUT').mockResolvedValue(fail(403, body) as never);
    expect(await replaceTeam('fan-a', draft, 'key', 1)).toEqual({ kind });
  });

  it.each([
    [{ type: 'https://touchline.example/problems/read-only-team', title: 'Anything' }, 'readOnly'],
    [{ type: 'https://touchline.example/problems/wrong-edit-key', title: 'Anything' }, 'wrongKey'],
    [{ title: 'Something new' }, 'forbidden'],
  ])('maps a DELETE 403 with %o to %s', async (body, kind) => {
    vi.spyOn(api, 'DELETE').mockResolvedValue(fail(403, body) as never);
    expect(await deleteTeam('fan-a', 'key')).toEqual({ kind });
  });
});

describe('503s from the team calls', () => {
  afterEach(() => vi.restoreAllMocks());

  const unavailable = { type: 'https://touchline.example/problems/name-check-unavailable', title: 'Anything' };

  it('maps the name-check-unavailable type to namesUnavailable for create, save and name check', async () => {
    vi.spyOn(api, 'POST').mockResolvedValue(fail(503, unavailable) as never);
    vi.spyOn(api, 'PUT').mockResolvedValue(fail(503, unavailable) as never);

    expect(await createTeam(draft)).toEqual({ kind: 'namesUnavailable' });
    expect(await checkNames([{ kind: 'Team', name: 'Harbour Town' }])).toEqual({ kind: 'namesUnavailable' });
    expect(await replaceTeam('fan-a', draft, 'key', 1)).toEqual({ kind: 'namesUnavailable' });
  });

  it('treats any other 503 as a plain error', async () => {
    vi.spyOn(api, 'POST').mockResolvedValue(fail(503, { title: 'Names can not be checked' }) as never);
    vi.spyOn(api, 'PUT').mockResolvedValue(fail(503, undefined) as never);

    expect(await createTeam(draft)).toEqual({ kind: 'error' });
    expect(await checkNames([{ kind: 'Team', name: 'Harbour Town' }])).toEqual({ kind: 'error' });
    expect(await replaceTeam('fan-a', draft, 'key', 1)).toEqual({ kind: 'error' });
  });
});
