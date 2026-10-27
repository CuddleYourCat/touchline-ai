import { act, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '../api/client';
import type { MatchDetail } from '../live/matchStreamReducer';
import { awaitsModelRecap, recapMaxPolls, recapPollMs, type MatchRecap } from '../recap/useRecap';
import { viewerStorageKey } from '../viewer/viewer';
import { RecapPage } from './RecapPage';

function detail(): MatchDetail {
  return {
    summary: {
      id: 'm1',
      kind: 'Fixture',
      status: 'FullTime',
      home: { id: 'h', name: 'Harbourside Rovers', shortName: 'HBR', crestUrl: '/crests/h.svg' },
      away: { id: 'a', name: 'Millbrook Athletic', shortName: 'MLB', crestUrl: '/crests/a.svg' },
      score: { home: 2, away: 1 },
      clock: { period: 'SecondHalf', minute: 90, second: 0, display: '90:00' },
      kickOffUtc: '2026-10-23T14:00:00Z',
      season: '2026',
      round: 3,
      dataSource: 'synthetic',
    },
    venue: 'Quayside Park',
    referee: 'Sam Ellery',
    homeLineup: [],
    homeSubstitutes: [],
    awaySubstitutes: [],
    awayLineup: [],
    state: { fullTime: true },
  } as unknown as MatchDetail;
}

const template: MatchRecap = {
  matchId: 'm1',
  headline: 'Harbourside Rovers 2–1 Millbrook Athletic',
  text: 'Full time: Harbourside Rovers 2, Millbrook Athletic 1. The key moments: 23:00: Goal.',
  mode: 'Casual',
  language: 'en',
  source: 'Template',
};

const model: MatchRecap = {
  matchId: 'm1',
  headline: 'Rovers edge a tense one at Quayside',
  text: 'Harbourside Rovers started fast.\n\nMillbrook pulled one back, but never found a leveller.',
  mode: 'Casual',
  language: 'en',
  source: 'Model',
};

const ok = (data: unknown) => ({ data, error: undefined, response: new Response() });
const status = (code: number) => ({ data: undefined, error: { detail: 'secret stack trace' }, response: new Response(null, { status: code }) });

/** Stubs the detail and gives each recap call the next response, the last one repeating. */
function stubRecaps(...recaps: unknown[]) {
  let call = 0;
  return vi.spyOn(api, 'GET').mockImplementation(((path: string) =>
    Promise.resolve(path === '/api/matches/{matchId}' ? ok(detail()) : recaps[Math.min(call++, recaps.length - 1)])) as never);
}

function recapCalls(get: ReturnType<typeof stubRecaps>) {
  return (get.mock.calls as unknown[][]).filter(([path]) => path === '/api/matches/{matchId}/recap');
}

function renderPage(id = 'm1') {
  return render(
    <MemoryRouter initialEntries={[`/matches/${id}/recap`]}>
      <Routes>
        <Route path="/matches/:id/recap" element={<RecapPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

/** Lets pending promises settle, and moves fake time on by `ms`. */
async function flush(ms = 0) {
  await act(() => vi.advanceTimersByTimeAsync(ms));
}

describe('RecapPage', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
    localStorage.clear();
  });

  it('has no grey-pitch option, as the recap has no pitch', async () => {
    stubRecaps(ok(model));

    renderPage();
    await flush();

    fireEvent.click(screen.getByRole('button', { name: 'Settings' }));

    expect(screen.getByRole('combobox', { name: 'Language' })).toBeInTheDocument();
    expect(screen.queryByRole('checkbox', { name: 'Grey pitch' })).not.toBeInTheDocument();
  });

  it('shows the model recap, paragraph by paragraph, for the saved viewer', async () => {
    localStorage.setItem(viewerStorageKey, JSON.stringify({ mode: 'Analyst', language: 'es' }));
    const get = stubRecaps(ok(model));

    renderPage();
    await flush();

    expect(screen.getByRole('heading', { name: 'Rovers edge a tense one at Quayside' })).toBeInTheDocument();
    expect(screen.getByText('Harbourside Rovers started fast.')).toBeInTheDocument();
    expect(screen.getByText('Millbrook pulled one back, but never found a leveller.')).toBeInTheDocument();
    expect(screen.queryByText(/Full recap on its way/)).not.toBeInTheDocument();
    expect(get).toHaveBeenCalledWith('/api/matches/{matchId}/recap', {
      params: { path: { matchId: 'm1' }, query: { mode: 'Analyst', lang: 'es' } },
    });
  });

  it('shows the template with "Full recap on its way", and swaps in the model recap when it arrives', async () => {
    const get = stubRecaps(ok(template), ok(template), ok(model));

    renderPage();
    await flush();

    expect(screen.getByRole('heading', { name: template.headline })).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Full recap on its way');
    expect(screen.getByText('Quick recap')).toBeInTheDocument();

    await flush(recapPollMs);
    expect(recapCalls(get)).toHaveLength(2);
    expect(screen.getByText(/Full recap on its way/)).toBeInTheDocument();

    await flush(recapPollMs);
    expect(recapCalls(get)).toHaveLength(3);
    expect(screen.getByRole('heading', { name: model.headline })).toBeInTheDocument();
    expect(screen.queryByText(/Full recap on its way/)).not.toBeInTheDocument();

    await flush(recapPollMs * 3);
    expect(recapCalls(get)).toHaveLength(3);
  });

  it('stops checking after a bounded number of tries, keeps the quick recap, and can check again', async () => {
    const get = stubRecaps(ok(template));

    renderPage();
    await flush();
    for (let i = 0; i < recapMaxPolls + 3; i++) await flush(recapPollMs);

    expect(recapCalls(get)).toHaveLength(recapMaxPolls + 1);
    expect(screen.getByText(/taking longer than usual/)).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: template.headline })).toBeInTheDocument();

    get.mockImplementation(((path: string) => Promise.resolve(path === '/api/matches/{matchId}' ? ok(detail()) : ok(model))) as never);
    fireEvent.click(screen.getByRole('button', { name: 'Check again' }));
    await flush();

    expect(screen.getByRole('heading', { name: model.headline })).toBeInTheDocument();
  });

  it('stops checking when the page goes away', async () => {
    const get = stubRecaps(ok(template));
    const { unmount } = renderPage();
    await flush();

    unmount();
    await flush(recapPollMs * 5);

    expect(recapCalls(get)).toHaveLength(1);
  });

  it('says the recap comes after the final whistle on a 409', async () => {
    stubRecaps(status(409));

    renderPage();
    await flush();

    expect(screen.getByText(/written after the final whistle/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Back to the match' })).toHaveAttribute('href', '/matches/m1');
  });

  it('degrades cleanly on a 503: the final score stays, with no server text and a retry', async () => {
    stubRecaps(status(503));

    renderPage();
    await flush();

    expect(screen.getByRole('alert')).toHaveTextContent("The recap isn't available right now.");
    expect(screen.getByLabelText('Full time, Harbourside Rovers 2, Millbrook Athletic 1')).toBeInTheDocument();
    expect(screen.queryByText(/secret stack trace/)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument();
  });

  it('renders recap text as text, never as HTML', async () => {
    stubRecaps(ok({ ...model, text: '<script>alert(1)</script> A strange recap.' }));

    const { container } = renderPage();
    await flush();

    expect(screen.getByText('<script>alert(1)</script> A strange recap.')).toBeInTheDocument();
    expect(container.querySelector('script')).toBeNull();
  });
});

describe('RecapPage on a demo match', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('shows the demo template as the final recap: no "on its way", no checking again', async () => {
    const get = stubRecaps(ok({ ...template, matchId: 'demo-1' }));

    renderPage('demo-1');
    await flush();
    await flush(recapPollMs * 3);

    expect(screen.getByRole('heading', { name: template.headline })).toBeInTheDocument();
    expect(screen.queryByText(/on its way|taking longer/)).not.toBeInTheDocument();
    expect(recapCalls(get)).toHaveLength(1);
  });
});

describe('awaitsModelRecap', () => {
  it('goes by the source alone, and never waits on a demo match', () => {
    expect(awaitsModelRecap(template, 'm1')).toBe(true);
    expect(awaitsModelRecap(model, 'm1')).toBe(false);
    expect(awaitsModelRecap({ ...model, headline: template.headline, text: template.text }, 'm1')).toBe(false);
    expect(awaitsModelRecap(template, 'demo-3')).toBe(false);
  });
});
