import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '../api/client';
import { refreshIntervalMs, type MatchSummary } from '../matches/useMatches';
import { HomePage } from './HomePage';

function match(id: string, status: MatchSummary['status'], overrides: Partial<MatchSummary> = {}): MatchSummary {
  return {
    id,
    kind: 'Fixture',
    status,
    home: { id: `${id}-h`, name: `Harbourside ${id}`, shortName: `HBR${id}`, crestUrl: `/crests/${id}-h.svg` },
    away: { id: `${id}-a`, name: `Millbrook ${id}`, shortName: `MLB${id}`, crestUrl: `/crests/${id}-a.svg` },
    score: { home: 2, away: 1 },
    clock: { period: 'SecondHalf', minute: 67, second: 12, display: '67:12' },
    kickOffUtc: '2026-10-23T14:00:00Z',
    season: '2026',
    round: 3,
    dataSource: 'synthetic',
    ...overrides,
  } as MatchSummary;
}

function respondWith(matches: MatchSummary[]) {
  return { data: matches, error: undefined, response: new Response() } as never;
}

/** Every call to the stub asked for the list, with no params the page didn't mean to send. */
function expectListRequests(get: { mock: { calls: unknown[][] } }) {
  expect(get.mock.calls.length).toBeGreaterThan(0);
  for (const call of get.mock.calls) expect(call).toEqual(['/api/matches']);
}

function setHidden(hidden: boolean) {
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden });
  document.dispatchEvent(new Event('visibilitychange'));
}

function renderPage() {
  return render(
    <MemoryRouter>
      <HomePage />
    </MemoryRouter>,
  );
}

describe('HomePage', () => {
  beforeEach(() => {
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => false });
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it('groups live matches first, then full time, then scheduled', async () => {
    const get = vi
      .spyOn(api, 'GET')
      .mockResolvedValue(respondWith([match('1', 'Scheduled'), match('2', 'FullTime'), match('3', 'Live')]));

    renderPage();

    const headings = await screen.findAllByRole('heading', { level: 2 });
    expect(headings.map((h) => h.textContent)).toEqual(['Live', 'Full time', 'Scheduled']);
    expect(within(screen.getByRole('region', { name: 'Live' })).getByText('LIVE')).toBeInTheDocument();
    expectListRequests(get);
  });

  it('puts a status it does not know in an Other section, last', async () => {
    vi.spyOn(api, 'GET').mockResolvedValue(
      respondWith([match('1', 'Postponed' as MatchSummary['status']), match('2', 'Live')]),
    );

    renderPage();

    const headings = await screen.findAllByRole('heading', { level: 2 });
    expect(headings.map((h) => h.textContent)).toEqual(['Live', 'Other']);
    expect(
      within(screen.getByRole('region', { name: 'Other' })).getByRole('link', { name: /Harbourside 1/ }),
    ).toBeInTheDocument();
  });

  it('names each card with its status, clock and score', async () => {
    vi.spyOn(api, 'GET').mockResolvedValue(
      respondWith([match('1', 'Live'), match('2', 'FullTime'), match('3', 'Scheduled')]),
    );

    renderPage();

    expect(await screen.findByRole('link', { name: 'Live, 67:12, Harbourside 1 2, Millbrook 1 1' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Full time, Harbourside 2 2, Millbrook 2 1' })).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: /^Scheduled, Harbourside 3 versus Millbrook 3, kick-off .+/ }),
    ).toBeInTheDocument();
  });

  it('ignores a slow response that arrives after a newer one', async () => {
    let resolveSlow: (value: never) => void = () => undefined;
    const get = vi
      .spyOn(api, 'GET')
      .mockReturnValueOnce(new Promise<never>((resolve) => (resolveSlow = resolve)))
      .mockResolvedValue(respondWith([match('2', 'Live')]));

    renderPage();
    act(() => setHidden(true));
    act(() => setHidden(false));

    expect(await screen.findByRole('link', { name: /Harbourside 2/ })).toBeInTheDocument();

    await act(async () => {
      resolveSlow(respondWith([match('1', 'Live')]));
      await Promise.resolve();
    });

    expect(screen.getByRole('link', { name: /Harbourside 2/ })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /Harbourside 1/ })).not.toBeInTheDocument();
    expect(get).toHaveBeenCalledTimes(2);
    expectListRequests(get);
  });

  it('leaves out empty sections', async () => {
    vi.spyOn(api, 'GET').mockResolvedValue(respondWith([match('1', 'Live')]));

    renderPage();

    const headings = await screen.findAllByRole('heading', { level: 2 });
    expect(headings.map((h) => h.textContent)).toEqual(['Live']);
  });

  it('shows the score, clock, crests and details on a card that links to the match', async () => {
    const get = vi.spyOn(api, 'GET').mockResolvedValue(respondWith([match('7', 'Live')]));

    renderPage();

    const card = await screen.findByRole('link', { name: /Harbourside 7/ });
    expect(card).toHaveAttribute('href', '/matches/7');
    expect(within(card).getByText('2 - 1')).toBeInTheDocument();
    expect(within(card).getByText('67:12')).toBeInTheDocument();
    expect(within(card).getByText('HBR7')).toBeInTheDocument();
    expect(within(card).getByText('Round 3')).toBeInTheDocument();
    expect(within(card).getByText('synthetic')).toBeInTheDocument();
    expect(within(card).getByAltText('Harbourside 7 crest')).toHaveAttribute('src', '/crests/7-h.svg');
    expect(within(card).getByAltText('Millbrook 7 crest')).toHaveAttribute('src', '/crests/7-a.svg');
    expectListRequests(get);
  });

  it('shows the empty state when there are no matches', async () => {
    vi.spyOn(api, 'GET').mockResolvedValue(respondWith([]));

    renderPage();

    expect(
      await screen.findByText('No matches yet. The demo match kicks off as soon as the service starts.'),
    ).toBeInTheDocument();
  });

  it('shows a plain error without server text, and recovers on Try again', async () => {
    const get = vi
      .spyOn(api, 'GET')
      .mockResolvedValueOnce({ data: undefined, error: { detail: 'secret stack trace' }, response: new Response() } as never)
      .mockResolvedValue(respondWith([match('1', 'Live')]));

    renderPage();

    expect(await screen.findByRole('alert')).toHaveTextContent("We couldn't load the matches just now.");
    expect(screen.queryByText(/secret stack trace/)).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));

    expect(await screen.findByRole('link', { name: /Harbourside 1/ })).toBeInTheDocument();
    expect(get).toHaveBeenCalledTimes(2);
    expectListRequests(get);
  });

  it('shows the error state when the request throws', async () => {
    vi.spyOn(api, 'GET').mockRejectedValue(new Error('network down'));

    renderPage();

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.queryByText(/network down/)).not.toBeInTheDocument();
  });

  it('refreshes every 15 seconds and pauses while the tab is hidden', async () => {
    vi.useFakeTimers();
    const get = vi.spyOn(api, 'GET').mockResolvedValue(respondWith([match('1', 'Live')]));

    renderPage();
    await act(() => vi.advanceTimersByTimeAsync(0));
    expect(get).toHaveBeenCalledTimes(1);

    await act(() => vi.advanceTimersByTimeAsync(refreshIntervalMs));
    expect(get).toHaveBeenCalledTimes(2);

    act(() => setHidden(true));
    await act(() => vi.advanceTimersByTimeAsync(refreshIntervalMs * 4));
    expect(get).toHaveBeenCalledTimes(2);

    act(() => setHidden(false));
    await act(() => vi.advanceTimersByTimeAsync(0));
    expect(get).toHaveBeenCalledTimes(3);

    await act(() => vi.advanceTimersByTimeAsync(refreshIntervalMs));
    expect(get).toHaveBeenCalledTimes(4);
    expectListRequests(get);
  });
});
