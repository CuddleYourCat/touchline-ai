import { act, render, renderHook, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '../api/client';
import type { CommentaryLine, LiveState, MatchDetail, MomentView } from '../live/matchStreamReducer';
import { staleAfterMs, useMatchStream } from '../live/useMatchStream';
import { FakeEventSource } from '../test/FakeEventSource';
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
    expect(screen.getByRole('status')).toHaveTextContent('Reconnecting…');

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

  it('shows a plain error when the detail fails', async () => {
    stubApi({ data: undefined, error: { detail: 'secret stack trace' }, response: new Response(null, { status: 503 }) });

    renderPage();

    expect(await screen.findByRole('alert')).toHaveTextContent("We couldn't load this match just now.");
    expect(screen.queryByText(/secret stack trace/)).not.toBeInTheDocument();
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
    expect(screen.getByRole('status')).toHaveTextContent('Reconnecting…');
    await act(() => vi.advanceTimersByTimeAsync(999));
    expect(FakeEventSource.instances).toHaveLength(1);
    await act(() => vi.advanceTimersByTimeAsync(1));
    expect(FakeEventSource.instances).toHaveLength(2);
    expect(streamUrl().searchParams.get('after')).toBe('52');
    expect(screen.getByRole('status')).toHaveTextContent('Reconnecting…');

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
      params: { path: { matchId: 'm1' }, query: { mode: 'Casual', lang: 'es-ES' } },
    });
    expect(streamUrl().searchParams.get('mode')).toBe('Casual');
    expect(streamUrl().searchParams.get('lang')).toBe('es-ES');

    act(() => FakeEventSource.latest().emit('commentary', line(30, 'Model', 'Big chance')));
    expect(screen.getByText('Gran ocasión')).toBeInTheDocument();

    act(() => FakeEventSource.latest().emit('commentary', { ...line(30, 'Model', 'Ocasión clarísima'), language: 'es-ES' }));
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
});
