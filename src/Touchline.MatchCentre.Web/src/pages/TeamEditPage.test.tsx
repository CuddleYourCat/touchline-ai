import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { StrictMode } from 'react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { api } from '../api/client';
import { editKeyFor, rememberEditKey } from '../teams/editKeys';
import { detail, fail, ok, options } from '../test/teamFixtures';
import { TeamEditPage } from './TeamEditPage';

const key = 'k7Qe3v9xZp2LmN8sT4wYb6Rj1Hc5Ud0Fa-_GqWiEoKs';
const teamId = 'fan-harbour-town';

/** Stubs GET by path: the options, and the team (or a status for it). */
function stubGets(team: unknown = ok(detail(teamId, 3))) {
  return vi.spyOn(api, 'GET').mockImplementation(((path: string) => {
    if (path === '/api/team-builder/options') return Promise.resolve(ok(options()));
    if (path === '/api/teams/{teamId}') return Promise.resolve(team);
    if (path === '/api/team-builder/names') return Promise.resolve(ok({ kind: 'Team', names: ['Quayside Wanderers'] }));
    return Promise.resolve(fail(404));
  }) as never);
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/teams" element={<p>Teams list</p>} />
        <Route path="/teams/new" element={<TeamEditPage />} />
        <Route path="/teams/:id/edit" element={<TeamEditPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

function type(label: string | RegExp, value: string) {
  fireEvent.change(screen.getByLabelText(label), { target: { value } });
}

describe('TeamEditPage: creating a team', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
    localStorage.clear();
  });

  it('creates the team, shows the edit key once with a Copy button, and stores it for this browser', async () => {
    stubGets();
    const created = detail(teamId, 1);
    const post = vi.spyOn(api, 'POST').mockImplementation(((path: string) =>
      Promise.resolve(path === '/api/teams' ? ok({ team: created, editKey: key }, 201) : fail(500))) as never);
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });

    renderAt('/teams/new');
    await screen.findByRole('form', { name: 'New team' });
    type('Club name', 'Harbour Town');
    type('Short name', 'hbt');
    type('Ground', 'Quayside Park');
    type('Manager name', 'Rory Calloway');
    screen.getAllByLabelText(/^Player \d+ name$/).forEach((input, i) => fireEvent.change(input, { target: { value: `Player Number${i}` } }));
    fireEvent.click(screen.getByRole('button', { name: 'Create team' }));

    expect(await screen.findByLabelText('Your edit key')).toHaveValue(key);
    expect(screen.getByText(/This browser remembers the key/)).toBeInTheDocument();
    expect(editKeyFor(teamId)).toBe(key);
    const body = (post.mock.calls[0] as unknown as unknown[])[1] as { body: { name: string; shortName: string; squad: unknown[] } };
    expect(body.body).toMatchObject({ name: 'Harbour Town', shortName: 'HBT' });
    expect(body.body.squad).toHaveLength(16);

    fireEvent.click(screen.getByRole('button', { name: 'Copy' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(key));
    expect(await screen.findByText(/Copied\./)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('link', { name: 'Edit the team' }));
    await screen.findByRole('form', { name: 'Edit team' });
    expect(screen.queryByLabelText('Your edit key')).not.toBeInTheDocument();
    expect(document.body.innerHTML).not.toContain(key);
  });

  it("blocks a squad that doesn't meet the rules before calling the service", async () => {
    stubGets();
    const post = vi.spyOn(api, 'POST');

    renderAt('/teams/new');
    await screen.findByRole('form', { name: 'New team' });
    fireEvent.click(screen.getByRole('button', { name: 'Create team' }));

    expect(await screen.findByRole('alert')).toHaveTextContent("The squad doesn't meet the rules yet.");
    expect(screen.getByText('Every player named').closest('li')).toHaveClass('pill-bad');
    expect(post).not.toHaveBeenCalled();
  });

  it('checks names in one batch after a blur, never per keystroke, and shows a refusal on its field', async () => {
    vi.useFakeTimers();
    stubGets();
    const post = vi.spyOn(api, 'POST').mockImplementation(((path: string, init: { body: { names: { kind: string; name: string }[] } }) =>
      Promise.resolve(
        path === '/api/team-builder/names/check'
          ? ok({
              names: init.body.names.map((n) => ({ ...n, allowed: n.kind !== 'Venue', reason: n.kind === 'Venue' ? 'That name is too close to a real ground.' : null })),
            })
          : fail(500),
      )) as never);

    renderAt('/teams/new');
    await act(() => vi.advanceTimersByTimeAsync(0));
    type('Club name', 'Harbour Town');
    type('Club name', 'Harbour Town Rovers');
    fireEvent.blur(screen.getByLabelText('Club name'));
    type('Ground', 'Fake Arena');
    fireEvent.blur(screen.getByLabelText('Ground'));
    expect(post).not.toHaveBeenCalled();

    await act(() => vi.advanceTimersByTimeAsync(1_500));

    expect(post).toHaveBeenCalledTimes(1);
    expect(post).toHaveBeenCalledWith('/api/team-builder/names/check', {
      body: { names: [{ kind: 'Team', name: 'Harbour Town Rovers' }, { kind: 'Venue', name: 'Fake Arena' }] },
    });
    expect(screen.getByText('That name is too close to a real ground.')).toBeInTheDocument();
    expect(screen.getByLabelText('Ground')).toHaveAttribute('aria-invalid', 'true');

    fireEvent.blur(screen.getByLabelText('Club name'));
    await act(() => vi.advanceTimersByTimeAsync(20_000));
    expect(post).toHaveBeenCalledTimes(1);
  });

  it('fills unnamed players from suggestions, even under StrictMode', async () => {
    vi.spyOn(api, 'GET').mockImplementation(((path: string) =>
      Promise.resolve(
        path === '/api/team-builder/options'
          ? ok(options())
          : ok({ kind: 'Player', names: Array.from({ length: 10 }, (_, i) => `Suggested Player${String.fromCharCode(65 + i)}`) }),
      )) as never);

    render(
      <StrictMode>
        <MemoryRouter initialEntries={['/teams/new']}>
          <Routes>
            <Route path="/teams/new" element={<TeamEditPage />} />
          </Routes>
        </MemoryRouter>
      </StrictMode>,
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Suggest names for 16 unnamed' }));

    expect(await screen.findByRole('button', { name: 'Suggest names for 6 unnamed' })).toBeInTheDocument();
    expect(screen.getByLabelText('Player 1 name')).toHaveValue('Suggested PlayerA');
    expect(screen.getByLabelText('Player 10 name')).toHaveValue('Suggested PlayerJ');
    expect(screen.getByLabelText('Player 11 name')).toHaveValue('');
  });

  it('fills a suggested club name', async () => {
    stubGets();
    renderAt('/teams/new');
    await screen.findByRole('form', { name: 'New team' });

    fireEvent.click(screen.getByRole('button', { name: 'Suggest a club name' }));

    await waitFor(() => expect(screen.getByLabelText('Club name')).toHaveValue('Quayside Wanderers'));
  });
});

describe('TeamEditPage: editing a fan team', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    localStorage.clear();
  });

  it('sends the edit key header and If-Match with the loaded version, then shows Saved', async () => {
    rememberEditKey(teamId, key);
    stubGets();
    const put = vi.spyOn(api, 'PUT').mockResolvedValue(ok(detail(teamId, 4)) as never);

    renderAt(`/teams/${teamId}/edit`);
    await screen.findByRole('form', { name: 'Edit team' });
    type('Club name', 'Harbour Town Albion');
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText('Saved.')).toBeInTheDocument();
    const [path, init] = put.mock.calls[0] as unknown as [string, { params: { path: unknown; header: unknown }; body: { name: string; squad: { id?: string }[] } }];
    expect(path).toBe('/api/teams/{teamId}');
    expect(init.params).toEqual({ path: { teamId }, header: { 'Touchline-Edit-Key': key, 'If-Match': '"3"' } });
    expect(init.body.name).toBe('Harbour Town Albion');
    expect(init.body.squad[0]?.id).toBe(`${teamId}-p1`);
  });

  it('says the team changed on a 412, and reloads it on request', async () => {
    rememberEditKey(teamId, key);
    const get = stubGets();
    vi.spyOn(api, 'PUT').mockResolvedValue(fail(412) as never);

    renderAt(`/teams/${teamId}/edit`);
    await screen.findByRole('form', { name: 'Edit team' });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('This team changed since you loaded it.');
    const loads = (get.mock.calls as unknown[][]).filter(([p]) => p === '/api/teams/{teamId}').length;
    fireEvent.click(screen.getByRole('button', { name: 'Reload the team' }));
    await screen.findByRole('form', { name: 'Edit team' });
    expect((get.mock.calls as unknown[][]).filter(([p]) => p === '/api/teams/{teamId}').length).toBe(loads + 1);
  });

  it('forgets a wrong key on a 403 and asks for the key, keeping the changes', async () => {
    rememberEditKey(teamId, key);
    stubGets();
    const put = vi.spyOn(api, 'PUT').mockResolvedValue(fail(403, { title: 'Wrong edit key', detail: 'secret' }) as never);

    renderAt(`/teams/${teamId}/edit`);
    await screen.findByRole('form', { name: 'Edit team' });
    type('Club name', 'Harbour Town Albion');
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByRole('heading', { name: 'Enter an edit key' })).toBeInTheDocument();
    expect(screen.getByText(/Enter the team's key, then save again/)).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent("That edit key doesn't match this team.");
    expect(editKeyFor(teamId)).toBe(key);
    expect(screen.getByRole('button', { name: 'Forget this key on this device' })).toBeInTheDocument();
    expect(screen.getByLabelText('Club name')).toBeDisabled();
    expect(screen.getByLabelText('Club name')).toHaveValue('Harbour Town Albion');

    const other = key.replace('k7', 'z9');
    fireEvent.change(screen.getByLabelText('Edit key'), { target: { value: ` ${other} ` } });
    fireEvent.click(screen.getByRole('button', { name: 'Unlock' }));
    expect(screen.getByLabelText('Club name')).toBeEnabled();
    expect(editKeyFor(teamId)).toBe(key);

    put.mockResolvedValue(ok(detail(teamId, 4)) as never);
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    expect(await screen.findByText('Saved.')).toBeInTheDocument();
    expect((put.mock.calls[1] as unknown as [string, { params: { header: Record<string, string> } }])[1].params.header['Touchline-Edit-Key']).toBe(other);
    expect(editKeyFor(teamId)).toBe(other);
  });

  it('never stores a pasted key the service refused, and forgets a stored key only on request', async () => {
    rememberEditKey(teamId, key);
    stubGets();
    vi.spyOn(api, 'PUT').mockResolvedValue(fail(403, { title: 'Wrong edit key' }) as never);

    renderAt(`/teams/${teamId}/edit`);
    await screen.findByRole('form', { name: 'Edit team' });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await screen.findByRole('heading', { name: 'Enter an edit key' });

    const pasted = key.replace('k7', 'z9');
    fireEvent.change(screen.getByLabelText('Edit key'), { target: { value: pasted } });
    fireEvent.click(screen.getByRole('button', { name: 'Unlock' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await screen.findByRole('heading', { name: 'Enter an edit key' });
    expect(editKeyFor(teamId)).toBe(key);

    fireEvent.click(screen.getByRole('button', { name: 'Forget this key on this device' }));
    expect(editKeyFor(teamId)).toBeUndefined();
    expect(screen.queryByRole('button', { name: 'Forget this key on this device' })).not.toBeInTheDocument();
  });

  it('keeps the key and the form on a 403 it does not recognise', async () => {
    rememberEditKey(teamId, key);
    stubGets();
    vi.spyOn(api, 'PUT').mockResolvedValue(fail(403, { title: 'Forbidden' }) as never);

    renderAt(`/teams/${teamId}/edit`);
    await screen.findByRole('form', { name: 'Edit team' });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByRole('alert')).toHaveTextContent("We couldn't verify the edit key just now.");
    expect(editKeyFor(teamId)).toBe(key);
    expect(screen.getByLabelText('Club name')).toBeEnabled();
    expect(screen.queryByRole('heading', { name: 'Enter an edit key' })).not.toBeInTheDocument();
  });

  it('deletes with a pasted key without storing it', async () => {
    stubGets();
    const del = vi.spyOn(api, 'DELETE').mockResolvedValue({ data: undefined, error: undefined, response: new Response(null, { status: 204 }) } as never);

    renderAt(`/teams/${teamId}/edit`);
    await screen.findByRole('heading', { name: 'Enter an edit key' });
    fireEvent.change(screen.getByLabelText('Edit key'), { target: { value: key } });
    fireEvent.click(screen.getByRole('button', { name: 'Unlock' }));
    expect(editKeyFor(teamId)).toBeUndefined();
    fireEvent.click(screen.getByRole('button', { name: 'Delete team' }));
    fireEvent.click(screen.getByRole('button', { name: 'Yes, delete it' }));

    expect(await screen.findByText('Teams list')).toBeInTheDocument();
    expect(del).toHaveBeenCalledWith('/api/teams/{teamId}', { params: { path: { teamId }, header: { 'Touchline-Edit-Key': key } } });
    expect(editKeyFor(teamId)).toBeUndefined();
  });

  it('says a read-only team is read-only on that 403 title, keeping the key', async () => {
    rememberEditKey(teamId, key);
    stubGets();
    vi.spyOn(api, 'PUT').mockResolvedValue(fail(403, { title: 'Read-only team' }) as never);

    renderAt(`/teams/${teamId}/edit`);
    await screen.findByRole('form', { name: 'Edit team' });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('This team is read-only.');
    expect(editKeyFor(teamId)).toBe(key);
  });

  it('maps a 400 onto the fields it names', async () => {
    rememberEditKey(teamId, key);
    stubGets();
    vi.spyOn(api, 'PUT').mockResolvedValue(
      fail(400, { title: 'One or more validation errors occurred.', detail: 'secret', errors: { 'squad[2].name': ['That name is a real player.'], venue: ['Too long.'] } }) as never,
    );

    renderAt(`/teams/${teamId}/edit`);
    await screen.findByRole('form', { name: 'Edit team' });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    const summary = await screen.findByRole('list', { name: 'What needs changing' });
    expect(within(summary).getByText(/Player 3 name:/)).toBeInTheDocument();
    expect(screen.getByLabelText('Player 3 name')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByLabelText('Ground')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.queryByText(/secret/)).not.toBeInTheDocument();
  });

  it('asks before deleting, and says so when the team is playing', async () => {
    rememberEditKey(teamId, key);
    stubGets();
    const del = vi.spyOn(api, 'DELETE').mockResolvedValue(fail(409, { title: 'Team is playing' }) as never);

    renderAt(`/teams/${teamId}/edit`);
    await screen.findByRole('form', { name: 'Edit team' });
    fireEvent.click(screen.getByRole('button', { name: 'Delete team' }));
    expect(del).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Yes, delete it' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('playing a match right now');
    expect(del).toHaveBeenCalledWith('/api/teams/{teamId}', { params: { path: { teamId }, header: { 'Touchline-Edit-Key': key } } });

    del.mockResolvedValue({ data: undefined, error: undefined, response: new Response(null, { status: 204 }) } as never);
    fireEvent.click(screen.getByRole('button', { name: 'Delete team' }));
    fireEvent.click(screen.getByRole('button', { name: 'Yes, delete it' }));
    expect(await screen.findByText('Teams list')).toBeInTheDocument();
    expect(editKeyFor(teamId)).toBeUndefined();
  });

  it('shows a fan team read-only without a key, and unlocks it without storing the pasted key', async () => {
    stubGets();
    renderAt(`/teams/${teamId}/edit`);

    expect(await screen.findByRole('heading', { name: 'Enter an edit key' })).toBeInTheDocument();
    expect(screen.getByLabelText('Club name')).toBeDisabled();

    fireEvent.change(screen.getByLabelText('Edit key'), { target: { value: 'nope' } });
    fireEvent.click(screen.getByRole('button', { name: 'Unlock' }));
    expect(screen.getByText(/doesn't look like an edit key/)).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Edit key'), { target: { value: key } });
    fireEvent.click(screen.getByRole('button', { name: 'Unlock' }));

    expect(screen.getByLabelText('Club name')).toBeEnabled();
    expect(editKeyFor(teamId)).toBeUndefined();
    expect(screen.queryByRole('button', { name: 'Forget this key on this device' })).not.toBeInTheDocument();
  });

  it('prunes the stored key when the team 404s', async () => {
    rememberEditKey(teamId, key);
    stubGets(fail(404));

    renderAt(`/teams/${teamId}/edit`);

    expect(await screen.findByRole('heading', { name: 'Team not found' })).toBeInTheDocument();
    expect(editKeyFor(teamId)).toBeUndefined();
  });

  it('shows a built-in club as read-only', async () => {
    const builtIn = detail('north-bank-fc');
    builtIn.team = { ...builtIn.team, origin: 'BuiltIn', name: 'North Bank' };
    stubGets(ok(builtIn));

    renderAt('/teams/north-bank-fc/edit');

    expect(await screen.findByText(/Built-in clubs can't be changed/)).toBeInTheDocument();
    expect(screen.queryByRole('form')).not.toBeInTheDocument();
  });
});
