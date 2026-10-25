import { act, render, renderHook, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Link, MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '../api/client';
import { maintenanceDeps, maintenanceHeader, resetMaintenanceForTests } from '../api/maintenance';
import type { CommentaryLine, LiveState, MatchDetail, MomentView } from '../live/matchStreamReducer';
import { staleAfterMs, useMatchStream } from '../live/useMatchStream';
import { momentAnchor } from '../questions/askQuestion';
import { FakeEventSource } from '../test/FakeEventSource';
import { viewerStorageKey } from '../viewer/viewer';
import { MatchPage } from './MatchPage';

/** Counts the pitch's renders: it calls this hook once per render. */
const pitchRenders = vi.hoisted(() => ({ count: 0 }));
vi.mock('../pitch/useReducedMotion', () => ({
  useReducedMotion: () => {
    pitchRenders.count++;
    return false;
  },
}));

function liveState(overrides: Partial<LiveState> = {}): LiveState {
  return {
    matchId: 'm1',
    lastSequence: 40,
    clock: { period: 'FirstHalf', minute: 12, second: 5, display: '12:05' },
    score: { home: 0, away: 0 },
    home: {},
    away: {},
    fullTime: false,
    ...overrides,
  } as LiveState;
}

function detail(state: LiveState = liveState()): MatchDetail {
  return {
    summary: {
      id: 'm1',
      kind: 'Fixture',
      status: 'Live',
      home: { id: 'h', name: 'Harbourside Rovers', shortName: 'HBR', crestUrl: '/crests/h.svg' },
      away: { id: 'a', name: 'Millbrook Athletic', shortName: 'MLB', crestUrl: '/crests/a.svg' },
      score: state.score,
      clock: state.clock,
      kickOffUtc: '2026-10-23T14:00:00Z',
      season: '2026',
      round: 3,
      dataSource: 'synthetic',
    },
    venue: 'Quayside Park',
    referee: 'Sam Ellery',
    homeLineup: [],
    awayLineup: [],
    state,
  } as MatchDetail;
}

function moment(sequence: number, overrides: Partial<MomentView> = {}): MomentView {
  return {
    id: `m1:${sequence}`,
    tier: 'Key',
    kind: 'BigChance',
    sequence,
    clock: { period: 'FirstHalf', minute: 10, second: 0, display: `10:${String(sequence).padStart(2, '0')}` },
    team: 'Home',
    playerId: null,
    reason: `Reason ${sequence}`,
    commentary: null,
    ...overrides,
  } as MomentView;
}

function line(sequence: number, source: CommentaryLine['source'], text: string, mode: CommentaryLine['mode'] = 'Casual'): CommentaryLine {
  return { matchId: 'm1', momentId: `m1:${sequence}`, sequence, tier: 'Key', source, text, mode, language: 'en' };
}

function stubApi(detailResponse: unknown, moments: MomentView[] = []) {
  return vi.spyOn(api, 'GET').mockImplementation(((path: string) =>
    Promise.resolve(
      path === '/api/matches/{matchId}'
        ? detailResponse
        : { data: moments, error: undefined, response: new Response() },
    )) as never);
}

/** Like `stubApi`, but each moments call takes the next response, so a test can make the first ones fail. */
function stubApiWithMoments(detailResponse: unknown, momentsResponses: unknown[]) {
  let call = 0;
  return vi.spyOn(api, 'GET').mockImplementation(((path: string) => {
    if (path === '/api/matches/{matchId}') return Promise.resolve(detailResponse);
    const next = momentsResponses[Math.min(call++, momentsResponses.length - 1)];
    return next instanceof Error ? Promise.reject(next) : Promise.resolve(next);
  }) as never);
}

const failedMoments = { data: undefined, error: { detail: 'secret stack trace' }, response: new Response(null, { status: 503 }) };

function streamUrl(source = FakeEventSource.latest()): URL {
  return new URL(source.url, 'http://localhost');
}

function found(d: MatchDetail) {
  return { data: d, error: undefined, response: new Response() };
}

function renderPage(id = 'm1') {
  return render(
    <MemoryRouter initialEntries={[`/matches/${id}`]}>
      <Routes>
        <Route path="/matches/:id" element={<MatchPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('MatchPage', () => {
  beforeEach(() => {
    FakeEventSource.install();
    resetMaintenanceForTests();
    vi.spyOn(maintenanceDeps, 'fetch').mockResolvedValue(new Response(null, { status: 405 }));
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('loads the match, then streams from the last sequence and updates the score and timeline', async () => {
    const get = stubApi(found(detail()), [moment(30)]);

    renderPage();

    const scoreboard = await screen.findByRole('region', { name: /^Live, 12:05, Harbourside Rovers 0/ });
    expect(within(scoreboard).getByText('LIVE')).toBeInTheDocument();
    expect(within(scoreboard).getByAltText('Harbourside Rovers crest')).toHaveAttribute('src', '/crests/h.svg');
    expect(screen.getByText('Reason 30')).toBeInTheDocument();
    expect(get).toHaveBeenCalledWith('/api/matches/{matchId}', { params: { path: { matchId: 'm1' } } });
    expect(streamUrl().pathname).toBe('/api/matches/m1/stream');
    expect(streamUrl().searchParams.get('after')).toBe('40');

    const source = FakeEventSource.latest();
    act(() => {
      source.emit('moment', moment(45, { kind: 'Goal' }));
      source.emit('state', liveState({ lastSequence: 45, score: { home: 1, away: 0 }, clock: { period: 'FirstHalf', minute: 13, second: 0, display: '13:00' } }));
    });

    expect(screen.getByRole('region', { name: 'Live, 13:00, Harbourside Rovers 1, Millbrook Athletic 0' })).toBeInTheDocument();
    const items = within(screen.getByRole('region', { name: 'Moments' })).getAllByRole('listitem');
    expect(items[0]).toHaveTextContent('Goal');
    expect(items[0]).toHaveTextContent('Reason 45');
    expect(items[1]).toHaveTextContent('Reason 30');
  });

  it('replaces a template line with the model line, keeps the line mode and shows analysis', async () => {
    stubApi(found(detail()), [moment(30, { commentary: line(30, 'Template', 'Template text') })]);

    renderPage();

    expect(await screen.findByText('Template text')).toBeInTheDocument();
    expect(screen.getByText('Template')).toBeInTheDocument();

    const source = FakeEventSource.latest();
    act(() => source.emit('commentary', line(30, 'Model', 'Model text', 'Analyst')));

    expect(screen.getByText('Model text')).toBeInTheDocument();
    expect(screen.queryByText('Template text')).not.toBeInTheDocument();
    expect(screen.queryByText('Template')).not.toBeInTheDocument();
    expect(screen.getByText('Analyst view')).toBeInTheDocument();

    act(() => {
      source.emit('commentary', line(30, 'Template', 'Late template'));
      source.emit('analysis', { momentId: 'm1:30', text: 'Why it happened', citations: [] });
    });

    expect(screen.getByText('Model text')).toBeInTheDocument();
    expect(screen.getByText('Why it happened')).toBeInTheDocument();
  });

  it('closes the stream on full time', async () => {
    stubApi(found(detail()));

    renderPage();
    await screen.findByText('LIVE');

    const source = FakeEventSource.latest();
    act(() => source.emit('fulltime', liveState({ fullTime: true, score: { home: 3, away: 2 } })));

    expect(source.closed).toBe(true);
    expect(screen.getByRole('region', { name: 'Full time, Harbourside Rovers 3, Millbrook Athletic 2' })).toBeInTheDocument();
  });

  it('shows the reconnecting banner after 30 silent seconds, and clears it on the next message', async () => {
    vi.useFakeTimers();
    stubApi(found(detail()));

    renderPage();
    await act(() => vi.advanceTimersByTimeAsync(0));
    const source = FakeEventSource.latest();

    await act(() => vi.advanceTimersByTimeAsync(staleAfterMs - 1000));
    act(() => source.emit('heartbeat', { at: '2026-10-23T14:12:00Z' }));
    await act(() => vi.advanceTimersByTimeAsync(staleAfterMs - 1000));
    expect(screen.queryByText('Reconnecting…')).not.toBeInTheDocument();

    await act(() => vi.advanceTimersByTimeAsync(1000));
    expect(screen.getByText('Reconnecting…')).toHaveAttribute('role', 'status');

    act(() => source.emit('state', liveState()));
    expect(screen.queryByText('Reconnecting…')).not.toBeInTheDocument();
  });

  it('closes the stream on unmount', async () => {
    stubApi(found(detail()));

    const { unmount } = renderPage();
    await screen.findByText('LIVE');
    const source = FakeEventSource.latest();

    unmount();

    expect(source.closed).toBe(true);
  });

  it('shows the not-found state for an unknown match, without server text, and opens no stream', async () => {
    stubApi({ data: undefined, error: { detail: 'secret stack trace' }, response: new Response(null, { status: 404 }) });

    renderPage('nope');

    expect(await screen.findByText(/There's no match with that ID/)).toBeInTheDocument();
    expect(screen.queryByText(/secret stack trace/)).not.toBeInTheDocument();
    expect(FakeEventSource.instances).toHaveLength(0);
  });

  it('shows a scheduled fan match as kicking off in a moment, keeps the stream open, and goes live with the first state', async () => {
    const scheduled = detail(liveState({ lastSequence: 0, clock: { period: 'FirstHalf', minute: 0, second: 0, display: '00:00' } }));
    stubApi(found({ ...scheduled, summary: { ...scheduled.summary, kind: 'Exhibition', status: 'Scheduled' } }));

    renderPage();

    expect(await screen.findByText('Kick-off in a moment…')).toBeInTheDocument();
    expect(screen.queryByText('LIVE')).not.toBeInTheDocument();
    const source = FakeEventSource.latest();
    expect(streamUrl(source).searchParams.get('after')).toBe('0');

    act(() => source.emit('heartbeat', {}));
    expect(screen.getByText('Kick-off in a moment…')).toBeInTheDocument();

    act(() => source.emit('state', liveState({ lastSequence: 1, clock: { period: 'FirstHalf', minute: 0, second: 1, display: '00:01' } }), 1));
    expect(await screen.findByText('LIVE')).toBeInTheDocument();
    expect(screen.queryByText('Kick-off in a moment…')).not.toBeInTheDocument();
  });

  it('says calmly that a fan match did not start on a 410, without server text, and opens no stream', async () => {
    stubApi({ data: undefined, error: { detail: 'secret stack trace' }, response: new Response(null, { status: 410 }) });

    renderPage('exh-202610241530-0a1b2c3d');

    expect(await screen.findByRole('heading', { name: "This match didn't start" })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Pick the teams again' })).toHaveAttribute('href', '/kickoff');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByText(/secret stack trace/)).not.toBeInTheDocument();
    expect(FakeEventSource.instances).toHaveLength(0);
  });

  it('shows a plain error when the detail fails', async () => {
    stubApi({ data: undefined, error: { detail: 'secret stack trace' }, response: new Response(null, { status: 503 }) });

    renderPage();

    expect(await screen.findByRole('alert')).toHaveTextContent("We couldn't load this match just now.");
    expect(screen.queryByText(/secret stack trace/)).not.toBeInTheDocument();
  });

  it('reloads onto the maintenance page when a dropped stream turns out to be maintenance', async () => {
    const reload = vi.spyOn(maintenanceDeps, 'reload').mockImplementation(() => {});
    const probe = vi.spyOn(maintenanceDeps, 'fetch').mockResolvedValue(
      new Response(null, { status: 503, headers: { [maintenanceHeader]: 'on' } }),
    );
    stubApi(found(detail()));

    renderPage();
    await waitFor(() => expect(FakeEventSource.instances).toHaveLength(1));
    act(() => FakeEventSource.latest().fail());

    await waitFor(() => expect(reload).toHaveBeenCalledTimes(1));
    expect(probe).toHaveBeenCalledWith('/api/matches', { method: 'HEAD', cache: 'no-store' });
  });

  it('reopens a stream the browser gave up on, with backoff, resuming after the last id seen', async () => {
    vi.useFakeTimers();
    vi.spyOn(Math, 'random').mockReturnValue(0);
    stubApi(found(detail()));

    renderPage();
    await act(() => vi.advanceTimersByTimeAsync(0));
    const first = FakeEventSource.latest();
    act(() => first.emit('state', liveState({ lastSequence: 52 }), 52));

    act(() => first.fail());
    expect(first.closed).toBe(true);
    expect(screen.getByText('Reconnecting…')).toHaveAttribute('role', 'status');
    await act(() => vi.advanceTimersByTimeAsync(999));
    expect(FakeEventSource.instances).toHaveLength(1);
    await act(() => vi.advanceTimersByTimeAsync(1));
    expect(FakeEventSource.instances).toHaveLength(2);
    expect(streamUrl().searchParams.get('after')).toBe('52');
    expect(screen.getByText('Reconnecting…')).toHaveAttribute('role', 'status');

    act(() => FakeEventSource.latest().fail());
    await act(() => vi.advanceTimersByTimeAsync(1999));
    expect(FakeEventSource.instances).toHaveLength(2);
    await act(() => vi.advanceTimersByTimeAsync(1));
    expect(FakeEventSource.instances).toHaveLength(3);

    act(() => FakeEventSource.latest().emit('state', liveState({ lastSequence: 60 }), 60));
    expect(screen.queryByText('Reconnecting…')).not.toBeInTheDocument();

    act(() => FakeEventSource.latest().fail());
    await act(() => vi.advanceTimersByTimeAsync(1000));
    expect(FakeEventSource.instances).toHaveLength(4);
    expect(streamUrl().searchParams.get('after')).toBe('60');
  });

  it('never reopens after full time or after unmount', async () => {
    vi.useFakeTimers();
    stubApi(found(detail()));

    const { unmount } = renderPage();
    await act(() => vi.advanceTimersByTimeAsync(0));
    const source = FakeEventSource.latest();
    act(() => source.emit('fulltime', liveState({ fullTime: true }), 99));
    act(() => source.fail());
    await act(() => vi.advanceTimersByTimeAsync(60_000));
    expect(FakeEventSource.instances).toHaveLength(1);

    unmount();
    FakeEventSource.install();
    stubApi(found(detail()));
    const second = renderPage();
    await act(() => vi.advanceTimersByTimeAsync(0));
    act(() => FakeEventSource.latest().fail());
    second.unmount();
    await act(() => vi.advanceTimersByTimeAsync(60_000));
    expect(FakeEventSource.instances).toHaveLength(1);
  });

  it('retries the moments once, then shows a notice whose Try again merges them in', async () => {
    const get = stubApiWithMoments(found(detail()), [failedMoments, new Error('network'), [moment(30)]].map((r) =>
      Array.isArray(r) ? { data: r, error: undefined, response: new Response() } : r,
    ));

    renderPage();

    expect(await screen.findByText("Earlier moments couldn't be loaded.")).toBeInTheDocument();
    expect((get.mock.calls as unknown as [string][]).filter(([path]) => path === '/api/matches/{matchId}/moments')).toHaveLength(2);
    expect(screen.queryByText(/secret stack trace/)).not.toBeInTheDocument();
    act(() => FakeEventSource.latest().emit('moment', moment(45)));

    act(() => screen.getByRole('button', { name: 'Try again' }).click());

    expect(await screen.findByText('Reason 30')).toBeInTheDocument();
    expect(screen.getByText('Reason 45')).toBeInTheDocument();
    expect(screen.queryByText("Earlier moments couldn't be loaded.")).not.toBeInTheDocument();
  });

  it('recovers silently when the moments retry succeeds', async () => {
    stubApiWithMoments(found(detail()), [failedMoments, { data: [moment(30)], error: undefined, response: new Response() }]);

    renderPage();

    expect(await screen.findByText('Reason 30')).toBeInTheDocument();
    expect(screen.queryByText("Earlier moments couldn't be loaded.")).not.toBeInTheDocument();
  });

  it("sends the viewer's mode and language, and keeps a Spanish line when an English one arrives later", async () => {
    vi.spyOn(navigator, 'language', 'get').mockReturnValue('es-ES');
    const spanish: CommentaryLine = { ...line(30, 'Template', 'Gran ocasión'), language: 'es' };
    const get = stubApi(found(detail()), [moment(30, { commentary: spanish })]);

    renderPage();

    expect(await screen.findByText('Gran ocasión')).toBeInTheDocument();
    expect(get).toHaveBeenCalledWith('/api/matches/{matchId}/moments', {
      params: { path: { matchId: 'm1' }, query: { mode: 'Casual', lang: 'es' } },
    });
    expect(streamUrl().searchParams.get('mode')).toBe('Casual');
    expect(streamUrl().searchParams.get('lang')).toBe('es');

    act(() => FakeEventSource.latest().emit('commentary', line(30, 'Model', 'Big chance')));
    expect(screen.getByText('Gran ocasión')).toBeInTheDocument();

    act(() => FakeEventSource.latest().emit('commentary', { ...line(30, 'Model', 'Ocasión clarísima'), language: 'es' }));
    expect(screen.getByText('Ocasión clarísima')).toBeInTheDocument();
  });

  it('puts the pitch and momentum between the scoreboard and the timeline, and draws streamed shapes, events and momentum', async () => {
    stubApi(found(detail()));

    renderPage();
    const scoreboard = await screen.findByRole('region', { name: /^Live/ });
    const pitch = screen.getByRole('region', { name: 'Pitch' });
    const momentum = screen.getByRole('region', { name: 'Momentum' });
    const timeline = screen.getByRole('region', { name: 'Moments' });
    expect(scoreboard.compareDocumentPosition(pitch) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(pitch.compareDocumentPosition(momentum) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(momentum.compareDocumentPosition(timeline) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(within(pitch).getByText('Modelled positions, not tracking data')).toBeInTheDocument();
    expect(within(pitch).queryAllByTestId('player-dot')).toHaveLength(0);

    const positions = (['Home', 'Away'] as const).flatMap((team) =>
      Array.from({ length: 11 }, (_, i) => ({ playerId: `${team}${i}`, team, x: 10 + i * 8, y: 34 })),
    );
    const source = FakeEventSource.latest();
    act(() => {
      source.emit('shape', { sequence: 41, positions });
      source.emit('event', {
        sequence: 41,
        matchId: 'm1',
        possessionId: 3,
        time: { period: 'FirstHalf', clock: '12:06' },
        type: 'Carry',
        team: 'Home',
        start: { x: 30, y: 20 },
        end: { x: 40, y: 20 },
      });
      source.emit('momentum', { period: 'FirstHalf', fromMinute: 10, toMinute: 15, value: 0.4 });
    });

    expect(within(pitch).getAllByTestId('player-dot')).toHaveLength(22);
    expect(within(pitch).getByTestId('ball-path')).toHaveAttribute('d', 'M 30 48 L 40 48');
    expect(within(momentum).getAllByTestId('momentum-bar')).toHaveLength(1);

    act(() => source.emit('shape', { sequence: 40, positions: positions.map((p) => ({ ...p, x: 0 })) }));
    expect(within(pitch).getAllByTestId('player-dot')[0]!.style.transform).toBe('translate(10px, 34px)');
  });

  it('does not re-render the pitch for a commentary line', async () => {
    stubApi(found(detail()), [moment(30, { commentary: line(30, 'Template', 'Template text') })]);

    renderPage();
    await screen.findByText('Template text');
    const before = pitchRenders.count;

    act(() => FakeEventSource.latest().emit('commentary', line(30, 'Model', 'Model text')));

    expect(screen.getByText('Model text')).toBeInTheDocument();
    expect(pitchRenders.count).toBe(before);

    act(() => FakeEventSource.latest().emit('event', {
      sequence: 41,
      matchId: 'm1',
      possessionId: 3,
      time: { period: 'FirstHalf', clock: '12:06' },
      type: 'Pass',
      team: 'Home',
      start: { x: 30, y: 20 },
      end: { x: 40, y: 20 },
    }));
    expect(pitchRenders.count).toBeGreaterThan(before);
  });

  it('tags a line only when it was written for the other mode', async () => {
    stubApi(found(detail()), [
      moment(30, { commentary: line(30, 'Model', 'Casual line', 'Casual') }),
      moment(31, { commentary: line(31, 'Model', 'Analyst line', 'Analyst') }),
    ]);

    renderPage();

    const items = within(await screen.findByRole('region', { name: 'Moments' })).getAllByRole('listitem');
    expect(items[0]).toHaveTextContent('Analyst view');
    expect(items[1]).not.toHaveTextContent(/view/);
  });
});

describe('useMatchStream', () => {
  beforeEach(() => {
    FakeEventSource.install();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('passes an explicit viewer to the moments and stream requests', async () => {
    const get = stubApi(found(detail()));

    const { result } = renderHook(() => useMatchStream('m1', { mode: 'Analyst', language: 'fr' }));

    await waitFor(() => expect(result.current.state.kind).toBe('ready'));
    expect(get).toHaveBeenCalledWith('/api/matches/{matchId}/moments', {
      params: { path: { matchId: 'm1' }, query: { mode: 'Analyst', lang: 'fr' } },
    });
    expect(streamUrl().searchParams.get('mode')).toBe('Analyst');
    expect(streamUrl().searchParams.get('lang')).toBe('fr');
  });

  it("sends a favourite club only when it's playing in the match", async () => {
    stubApi(found(detail()));

    const playing = renderHook(() => useMatchStream('m1', { mode: 'Casual', language: 'en', team: 'h' }));
    await waitFor(() => expect(playing.result.current.state.kind).toBe('ready'));
    expect(streamUrl().searchParams.get('team')).toBe('h');
    expect(playing.result.current.viewer.team).toBe('h');
    playing.unmount();

    const elsewhere = renderHook(() => useMatchStream('m1', { mode: 'Casual', language: 'en', team: 'some-other-club' }));
    await waitFor(() => expect(elsewhere.result.current.state.kind).toBe('ready'));
    expect(streamUrl().searchParams.has('team')).toBe(false);
    expect(elsewhere.result.current.viewer.team).toBeUndefined();
  });
});

describe('MatchPage viewer, Q&A and recap', () => {
  beforeEach(() => {
    FakeEventSource.install();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  it('switches to Analyst, saves the choice, and reopens the stream for it without blanking the match', async () => {
    stubApi(found(detail()), [moment(30)]);
    renderPage();
    await screen.findByText('LIVE');
    const first = FakeEventSource.latest();
    expect(streamUrl(first).searchParams.get('mode')).toBe('Casual');

    const user = userEvent.setup();
    await user.click(screen.getByRole('radio', { name: 'Analyst' }));

    expect(screen.getByText('LIVE')).toBeInTheDocument();
    await waitFor(() => expect(FakeEventSource.instances).toHaveLength(2));
    expect(first.closed).toBe(true);
    expect(streamUrl().searchParams.get('mode')).toBe('Analyst');
    expect(JSON.parse(localStorage.getItem(viewerStorageKey) ?? '{}')).toMatchObject({ mode: 'Analyst' });
  });

  it('keeps the pitch, trail and momentum across a viewer change, and replaces only the moments and commentary', async () => {
    const get = stubApi(found(detail()), [moment(30, { commentary: line(30, 'Template', 'Casual words') })]);
    renderPage();
    await screen.findByText('Casual words');

    const positions = (['Home', 'Away'] as const).flatMap((team) =>
      Array.from({ length: 11 }, (_, i) => ({ playerId: `${team}${i}`, team, x: 10 + i * 8, y: 34 })),
    );
    const carry = (sequence: number, x: number) => ({
      sequence,
      matchId: 'm1',
      possessionId: 3,
      time: { period: 'FirstHalf', clock: '12:06' },
      type: 'Carry',
      team: 'Home',
      start: { x, y: 20 },
      end: { x: x + 10, y: 20 },
    });
    act(() => {
      const source = FakeEventSource.latest();
      source.emit('shape', { sequence: 43, positions });
      source.emit('event', carry(41, 10));
      source.emit('event', carry(42, 30));
      source.emit('event', carry(43, 50));
      source.emit('momentum', { period: 'FirstHalf', fromMinute: 10, toMinute: 15, value: 0.4 });
      source.emit('analysis', { momentId: 'm1:30', text: 'Why it happened', citations: [] });
    });
    const pitch = screen.getByRole('region', { name: 'Pitch' });
    const trail = within(pitch).getAllByTestId('ball-trail-layer').map((l) => l.getAttribute('d'));
    expect(trail).not.toHaveLength(0);

    get.mockImplementation(((path: string) =>
      Promise.resolve(
        path === '/api/matches/{matchId}'
          ? found(detail())
          : { data: [moment(30, { commentary: line(30, 'Template', 'Analyst words', 'Analyst') })], error: undefined, response: new Response() },
      )) as never);
    const user = userEvent.setup();
    await user.click(screen.getByRole('radio', { name: 'Analyst' }));

    expect(await screen.findByText('Analyst words')).toBeInTheDocument();
    expect(screen.queryByText('Casual words')).not.toBeInTheDocument();
    expect(within(pitch).getAllByTestId('player-dot')).toHaveLength(22);
    expect(within(pitch).getByTestId('ball-path')).toHaveAttribute('d', 'M 50 48 L 60 48');
    expect(within(pitch).getAllByTestId('ball-trail-layer').map((l) => l.getAttribute('d'))).toEqual(trail);
    expect(within(screen.getByRole('region', { name: 'Momentum' })).getAllByTestId('momentum-bar')).toHaveLength(1);
    expect(screen.getByText('Why it happened')).toBeInTheDocument();
  });

  it("drops a question in flight when the match changes, so its answer can't land on the next match", async () => {
    stubApi(found(detail()));
    let signal: AbortSignal | undefined;
    let answer!: (value: unknown) => void;
    vi.spyOn(api, 'POST').mockImplementation(((_path: string, init: { signal: AbortSignal }) => {
      signal = init.signal;
      return new Promise((resolve) => (answer = resolve));
    }) as never);
    render(
      <MemoryRouter initialEntries={['/matches/m1']}>
        <Link to="/matches/m2">Next match</Link>
        <Routes>
          <Route path="/matches/:id" element={<MatchPage />} />
        </Routes>
      </MemoryRouter>,
    );
    await screen.findByText('LIVE');

    const user = userEvent.setup();
    await user.type(screen.getByRole('textbox', { name: 'Your question about this match' }), 'Who scored?');
    await user.click(screen.getByRole('button', { name: 'Ask' }));
    expect(screen.getByRole('list', { name: 'Answers' })).toHaveTextContent('Who scored?');

    await user.click(screen.getByRole('link', { name: 'Next match' }));
    await screen.findByText('LIVE');
    await act(async () => answer({ data: { text: 'An answer about match one.', citations: [], language: 'en' }, error: undefined, response: new Response() }));

    expect(signal?.aborted).toBe(true);
    expect(screen.queryByText(/An answer about match one/)).not.toBeInTheDocument();
    expect(screen.queryByText('Who scored?')).not.toBeInTheDocument();
  });

  it('offers only English, Spanish and French, and falls back to English for any other browser language', async () => {
    vi.spyOn(navigator, 'language', 'get').mockReturnValue('de-DE');
    stubApi(found(detail()));
    renderPage();
    await screen.findByText('LIVE');

    const select = screen.getByRole('combobox', { name: 'Language' });
    expect(within(select).getAllByRole('option').map((o) => o.getAttribute('value'))).toEqual(['en', 'es', 'fr']);
    expect(select).toHaveValue('en');
    expect(streamUrl().searchParams.get('lang')).toBe('en');
  });

  it('offers the two clubs as favourites and sends the one picked', async () => {
    stubApi(found(detail()));
    renderPage();
    await screen.findByText('LIVE');

    const user = userEvent.setup();
    await user.selectOptions(screen.getByRole('combobox', { name: 'Favourite club' }), 'Millbrook Athletic');

    await waitFor(() => expect(streamUrl().searchParams.get('team')).toBe('a'));
  });

  it('puts the Q&A box on the page and links to the recap at full time', async () => {
    stubApi(found(detail(liveState({ fullTime: true }))));
    renderPage();

    expect(await screen.findByRole('region', { name: 'Ask the Analyst' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Read the full-time recap/ })).toHaveAttribute('href', '/matches/m1/recap');
  });

  it('gives each moment an anchor a citation can jump to', async () => {
    stubApi(found(detail()), [moment(30)]);
    renderPage();
    await screen.findByText('Reason 30');

    expect(screen.getByText('Reason 30').closest('li')).toHaveAttribute('id', momentAnchor('m1:30'));
  });
});
