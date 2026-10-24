import { afterEach, describe, expect, it, vi } from 'vitest';
import { api } from '../api/client';
import { detail, fail } from '../test/teamFixtures';
import { draftFromForm, formFromDetail } from './teamDraft';
import { deleteTeam, replaceTeam } from './teamsApi';

const draft = draftFromForm(formFromDetail(detail()));

describe('403s from the team calls', () => {
  afterEach(() => vi.restoreAllMocks());

  it.each([
    [{ title: 'Read-only team' }, 'readOnly'],
    [{ title: 'Wrong edit key' }, 'wrongKey'],
    [{ title: 'Forbidden' }, 'forbidden'],
    [undefined, 'forbidden'],
  ])('maps a PUT 403 with %o to %s', async (body, kind) => {
    vi.spyOn(api, 'PUT').mockResolvedValue(fail(403, body) as never);
    expect(await replaceTeam('fan-a', draft, 'key', 1)).toEqual({ kind });
  });

  it.each([
    [{ title: 'Read-only team' }, 'readOnly'],
    [{ title: 'Wrong edit key' }, 'wrongKey'],
    [{ title: 'Something new' }, 'forbidden'],
  ])('maps a DELETE 403 with %o to %s', async (body, kind) => {
    vi.spyOn(api, 'DELETE').mockResolvedValue(fail(403, body) as never);
    expect(await deleteTeam('fan-a', 'key')).toEqual({ kind });
  });
});
