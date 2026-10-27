import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { api } from '../api/client';
import { detail, fail, ok, summary } from '../test/teamFixtures';
import { KickoffPage } from './KickoffPage';

const teams = [summary('north-bank-fc', 'North Bank', 'BuiltIn'), summary('fan-harbour-town', 'Harbour Town')];

function stubGets(status: unknown = ok({ service: 'touchline-ai', dataSource: 'synthetic', fanMatches: true })) {
  return vi.spyOn(api, 'GET').mockImplementation(((path: string, init?: { params?: { path?: { teamId?: string } } }) => {
    if (path === '/api/status') return Promise.resolve(status);
    if (path === '/api/teams') return Promise.resolve(ok(teams));
    if (path === '/api/teams/{teamId}') return Promise.resolve(ok(detail(init?.params?.path?.teamId ?? 'x')));
    return Promise.resolve(fail(404));
  }) as never);
}

type PostInit = { params: { header: { 'Idempotency-Key': string } }; body: { homeTeamId: string; awayTeamId: string; home: unknown; away: unknown } };

function stubPost(...responses: unknown[]) {
  let call = 0;
  return vi.spyOn(api, 'POST').mockImplementation((() => Promise.resolve(responses[Math.min(call++, responses.length - 1)])) as never);
}

function renderAt(path = '/kickoff?home=fan-harbour-town&away=north-bank-fc') {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/kickoff" element={<KickoffPage />} />
        <Route path="/matches/:id" element={<p>Match page</p>} />
      </Routes>
    </MemoryRouter>,
  );
}

const homeSide = () => screen.getByRole('group', { name: 'Home' });

describe('KickoffPage', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('shows Coming Soon with links, and no form, when fan matches are off', async () => {
    const get = stubGets(ok({ service: 'touchline-ai', dataSource: 'synthetic', fanMatches: false }));

    renderAt();

    expect(await screen.findByText('Coming Soon')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Matches' })).toHaveAttribute('href', '/');
    expect(screen.getByRole('link', { name: 'Build a team' })).toHaveAttribute('href', '/teams/new');
    expect(screen.queryByRole('form')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Kick off' })).not.toBeInTheDocument();
    expect(get).not.toHaveBeenCalledWith('/api/teams', expect.anything());
  });

  it('waits for the status before showing anything', () => {
    vi.spyOn(api, 'GET').mockImplementation((() => new Promise(() => undefined)) as never);

    renderAt();

    expect(screen.getByText('Checking the pitch...')).toBeInTheDocument();
    expect(screen.queryByRole('form')).not.toBeInTheDocument();
    expect(screen.queryByText('Coming Soon')).not.toBeInTheDocument();
  });

  it('shows the form when fan matches are on', async () => {
    stubGets();

    renderAt();

    expect(await screen.findByRole('form', { name: 'Kick off a match' })).toBeInTheDocument();
    expect(screen.queryByText('Coming Soon')).not.toBeInTheDocument();
  });

  it('shows the form when the status call fails', async () => {
    stubGets(fail(500));

    renderAt();

    expect(await screen.findByRole('form', { name: 'Kick off a match' })).toBeInTheDocument();
    expect(screen.queryByText('Coming Soon')).not.toBeInTheDocument();
  });

  it('takes the teams from the query, lets the engine pick by default, and opens the match on a 202', async () => {
    stubGets();
    const post = stubPost(ok({ matchId: 'fan-m7', status: 'Scheduled', streamUrl: '/api/matches/fan-m7/stream' }, 202));

    renderAt();
    expect(await screen.findByLabelText('Home team')).toHaveValue('fan-harbour-town');
    expect(screen.getByLabelText('Away team')).toHaveValue('north-bank-fc');
    expect(within(homeSide()).getByLabelText('Engine picks the line-up')).toBeChecked();

    fireEvent.click(screen.getByRole('button', { name: 'Kick off' }));

    expect(await screen.findByText('Match page')).toBeInTheDocument();
    const [path, init] = post.mock.calls[0] as unknown as [string, PostInit];
    expect(path).toBe('/api/matches');
    expect(init.body).toEqual({ homeTeamId: 'fan-harbour-town', awayTeamId: 'north-bank-fc', home: null, away: null });
    expect(init.params.header['Idempotency-Key']).toMatch(/^[0-9a-f-]{36}$/);
  });

  it('keeps the selections and shows a calm banner on a 503, with a fresh idempotency key per submit', async () => {
    stubGets();
    const post = stubPost(fail(503, { title: 'Service unavailable', detail: 'fan matches need the live pipeline' }));

    renderAt();
    await screen.findByLabelText('Home team');
    fireEvent.click(within(homeSide()).getByLabelText('Engine picks the line-up'));
    fireEvent.click(await within(homeSide()).findByRole('button', { name: 'Auto-pick' }));
    expect(within(homeSide()).getByText('4-4-2')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Kick off' }));

    expect(await screen.findByText("Fan matches can't be started just now. Your picks are still here, so try again later.")).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByText(/live pipeline/)).not.toBeInTheDocument();
    expect(screen.getByLabelText('Home team')).toHaveValue('fan-harbour-town');
    expect(within(homeSide()).getByLabelText('Engine picks the line-up')).not.toBeChecked();
    expect(within(homeSide()).getByLabelText(/Arlo Testwell/)).toHaveValue('starter');

    const sent = (post.mock.calls[0] as unknown as [string, PostInit])[1];
    expect(sent.body.home).toEqual({
      startingEleven: [1, 3, 4, 5, 6, 8, 9, 10, 11, 13, 14].map((n) => `fan-harbour-town-p${n}`),
      substitutes: [2, 7, 12, 15, 16].map((n) => `fan-harbour-town-p${n}`),
    });
    expect(sent.body.away).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Kick off' }));
    await waitFor(() => expect(post).toHaveBeenCalledTimes(2));
    const keys = post.mock.calls.map((c) => (c as unknown as [string, PostInit])[1].params.header['Idempotency-Key']);
    expect(keys[0]).not.toBe(keys[1]);
  });

  it('keeps one idempotency key per intended match: reused after an error, new when a team or sheet changes', async () => {
    stubGets();
    const post = stubPost(fail(500));
    const keys = () => post.mock.calls.map((c) => (c as unknown as [string, PostInit])[1].params.header['Idempotency-Key']);
    const submit = async (count: number) => {
      fireEvent.click(screen.getByRole('button', { name: 'Kick off' }));
      await waitFor(() => expect(post).toHaveBeenCalledTimes(count));
      await screen.findByRole('button', { name: 'Kick off' });
    };

    renderAt();
    await screen.findByLabelText('Home team');
    await submit(1);
    await submit(2);
    expect(keys()[1]).toBe(keys()[0]);

    fireEvent.change(screen.getByLabelText('Away team'), { target: { value: '' } });
    fireEvent.change(screen.getByLabelText('Away team'), { target: { value: 'north-bank-fc' } });
    await submit(3);
    expect(keys()[2]).not.toBe(keys()[1]);

    fireEvent.click(within(homeSide()).getByLabelText('Engine picks the line-up'));
    fireEvent.click(await within(homeSide()).findByRole('button', { name: 'Auto-pick' }));
    await submit(4);
    expect(keys()[3]).not.toBe(keys()[2]);
    await submit(5);
    expect(keys()[4]).toBe(keys()[3]);
  });

  it('disables the button while the request is in flight, so a double click sends once', async () => {
    stubGets();
    let resolve: (value: unknown) => void = () => {};
    const post = vi.spyOn(api, 'POST').mockImplementation((() => new Promise((r) => (resolve = r))) as never);

    renderAt();
    await screen.findByLabelText('Home team');
    const button = screen.getByRole('button', { name: 'Kick off' });
    fireEvent.click(button);
    fireEvent.click(button);

    expect(await screen.findByRole('button', { name: 'Starting...' })).toBeDisabled();
    expect(post).toHaveBeenCalledTimes(1);
    resolve(fail(503));
    expect(await screen.findByRole('button', { name: 'Kick off' })).toBeEnabled();
  });

  it('uses Retry-After on a 429', async () => {
    stubGets();
    stubPost(fail(429, { title: 'Too many matches', detail: 'secret' }, { 'Retry-After': '120' }));

    renderAt();
    await screen.findByLabelText('Home team');
    fireEvent.click(screen.getByRole('button', { name: 'Kick off' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Too many fan matches right now. Try again in about 2 minutes.');
    expect(screen.queryByText(/secret/)).not.toBeInTheDocument();
  });

  it("maps a 400's errors onto the side they belong to", async () => {
    stubGets();
    stubPost(fail(400, { title: 'One or more validation errors occurred.', errors: { home: ['A substitute is also starting.'], seed: ['Odd seed.'] } }));

    renderAt();
    await screen.findByLabelText('Home team');
    fireEvent.click(screen.getByRole('button', { name: 'Kick off' }));

    expect(await within(homeSide()).findByText('A substitute is also starting.')).toBeInTheDocument();
    expect(within(screen.getByRole('group', { name: 'Away' })).queryByText('A substitute is also starting.')).not.toBeInTheDocument();
    expect(screen.getByText('Odd seed.')).toBeInTheDocument();
  });

  it('says one team is unknown on a 404, and that a team cannot play itself', async () => {
    stubGets();
    const post = stubPost(fail(404));

    renderAt();
    await screen.findByLabelText('Home team');
    fireEvent.click(screen.getByRole('button', { name: 'Kick off' }));
    expect(await screen.findByRole('alert')).toHaveTextContent("One of those teams doesn't exist any more.");

    fireEvent.change(screen.getByLabelText('Away team'), { target: { value: 'fan-harbour-town' } });
    fireEvent.click(screen.getByRole('button', { name: 'Kick off' }));
    expect(await screen.findByRole('alert')).toHaveTextContent("A team can't play itself.");
    expect(post).toHaveBeenCalledTimes(1);
  });

  it("won't send an incomplete line-up, and ignores query values that aren't listed teams", async () => {
    stubGets();
    const post = stubPost(fail(503));

    renderAt('/kickoff?home=fan-harbour-town&away=not%20a%20team');
    await screen.findByLabelText('Home team');
    expect(screen.getByLabelText('Away team')).toHaveValue('');
    fireEvent.change(screen.getByLabelText('Away team'), { target: { value: 'north-bank-fc' } });
    fireEvent.click(within(homeSide()).getByLabelText('Engine picks the line-up'));
    const keeper = await within(homeSide()).findByLabelText(/Arlo Testwell/);
    fireEvent.change(keeper, { target: { value: 'starter' } });
    expect(within(homeSide()).getByText('XI 1/11')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Kick off' }));

    expect(await screen.findByRole('alert')).toHaveTextContent("The home line-up isn't ready yet");
    expect(post).not.toHaveBeenCalled();
  });
});
