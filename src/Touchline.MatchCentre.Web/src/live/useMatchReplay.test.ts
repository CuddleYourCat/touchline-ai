import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FakeEventSource } from '../test/FakeEventSource';
import {
  initialLiveMatch,
  type LiveMatch,
  type LiveState,
  type MatchDetail,
  type MatchEvent,
  type MomentView,
  type OverlayCard,
  type ShapeView,
} from './matchStreamReducer';
import { halfTimePauseMs, secondsBehind, useMatchReplay, type MatchReplay } from './useMatchReplay';

type Period = 'FirstHalf' | 'SecondHalf';

function clock(period: Period, minute: number, second = 0): LiveState['clock'] {
  return { period, minute, second, display: `${minute}:${String(second).padStart(2, '0')}` };
}

function state(lastSequence: number, at: LiveState['clock'], overrides: Partial<LiveState> = {}): LiveState {
  return { matchId: 'm1', lastSequence, clock: at, score: { home: 0, away: 0 }, home: {}, away: {}, fullTime: false, ...overrides } as LiveState;
}

function timeSpan(minute: number, second: number): string {
  return `00:${String(minute).padStart(2, '0')}:${String(second).padStart(2, '0')}`;
}

function event(sequence: number, period: Period, minute: number, second = 0, type: MatchEvent['type'] = 'Pass'): MatchEvent {
  return {
    sequence,
    matchId: 'm1',
    possessionId: 1,
    time: { period, clock: timeSpan(minute, second) },
    type,
    team: 'Home',
    playerId: 'h-9',
    start: { x: 50, y: 34 },
    end: { x: 60, y: 30 },
  } as MatchEvent;
}

function detail(at: LiveState): MatchDetail {
  return {
    summary: {
      id: 'm1',
      kind: 'Fixture',
      status: at.fullTime ? 'FullTime' : 'Live',
      home: { id: 'h', name: 'Harbourside Rovers', shortName: 'HBR', crestUrl: '/crests/h.svg' },
      away: { id: 'a', name: 'Millbrook Athletic', shortName: 'MLB', crestUrl: '/crests/a.svg' },
      score: at.score,
      clock: at.clock,
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
    state: at,
  } as MatchDetail;
}

function moment(sequence: number): MomentView {
  return {
    id: `m1:${sequence}`,
    tier: 'Key',
    kind: 'Goal',
    sequence,
    clock: clock('FirstHalf', 10),
    team: 'Home',
    playerId: null,
    reason: 'A goal',
    commentary: null,
  } as MomentView;
}

/** The live match at sequence 100, 60:00, 2-1 up, with a goal at 10 and one at 30. */
function liveAt(lastSequence = 100, at = clock('SecondHalf', 60), overrides: Partial<LiveState> = {}): LiveMatch {
  const now = state(lastSequence, at, { score: { home: 2, away: 1 }, ...overrides });
  return initialLiveMatch(detail(now), [moment(10), moment(30), moment(90)]);
}

/** Emits one event and the state it leads to, as the server does. */
function emitEvent(source: FakeEventSource, e: MatchEvent, score = { home: 0, away: 0 }) {
  const minute = Number(e.time.clock.split(':')[1]);
  const second = Number(e.time.clock.split(':')[2]);
  act(() => {
    source.emit('event', e, e.sequence);
    source.emit('state', state(e.sequence, clock(e.time.period, minute, second), { score }), e.sequence);
  });
}

function streamUrl(source: FakeEventSource): URL {
  return new URL(source.url, 'http://localhost');
}

const advance = (ms: number) => act(() => vi.advanceTimersByTime(ms));

function replayOf(live: LiveMatch | null, speed?: number) {
  return renderHook(
    ({ current }: { current: LiveMatch | null }) =>
      useMatchReplay('m1', current, { speed, viewer: { mode: 'Analyst', language: 'es', team: 'h' } }),
    { initialProps: { current: live } },
  );
}

const sequenceOf = (replay: MatchReplay) => replay.match?.state.lastSequence;

describe('useMatchReplay', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    FakeEventSource.install();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('is idle until started, with no stream open', () => {
    const { result } = replayOf(liveAt());

    expect(result.current.status).toBe('idle');
    expect(result.current.match).toBeNull();
    expect(result.current.clock).toBeNull();
    expect(result.current.behindLiveSeconds).toBeNull();
    expect(FakeEventSource.instances).toHaveLength(0);
  });

  it('opens a second stream two events before the chosen one, keeping every shape, for the live viewer', () => {
    const { result } = replayOf(liveAt());

    act(() => result.current.start({ sequence: 30 }));

    const url = streamUrl(FakeEventSource.latest());
    expect(url.pathname).toBe('/api/matches/m1/stream');
    expect(url.searchParams.get('after')).toBe('28');
    expect(url.searchParams.get('replay')).toBe('true');
    expect(url.searchParams.get('mode')).toBe('Analyst');
    expect(url.searchParams.get('lang')).toBe('es');
    expect(url.searchParams.get('team')).toBe('h');
    expect(result.current.status).toBe('loading');
  });

  it('never asks for a sequence below 0', () => {
    const { result } = replayOf(liveAt());

    act(() => result.current.start({ sequence: 1 }));

    expect(streamUrl(FakeEventSource.latest()).searchParams.get('after')).toBe('0');
  });

  it('starts from a match time with fromPeriod and fromMinute', () => {
    const { result } = replayOf(liveAt());

    act(() => result.current.start({ period: 'SecondHalf', minute: 52 }));

    const url = streamUrl(FakeEventSource.latest());
    expect(url.searchParams.get('fromPeriod')).toBe('SecondHalf');
    expect(url.searchParams.get('fromMinute')).toBe('52');
    expect(url.searchParams.has('after')).toBe(false);
    expect(url.searchParams.get('replay')).toBe('true');
  });

  it('does nothing before the live match has loaded', () => {
    const { result } = replayOf(null);

    act(() => result.current.start({ sequence: 30 }));

    expect(result.current.status).toBe('idle');
    expect(FakeEventSource.instances).toHaveLength(0);
  });

  it('plays the first event at once, then each one its match-time gap later, divided by the speed', () => {
    const { result } = replayOf(liveAt());
    act(() => result.current.start({ sequence: 30 }));
    const source = FakeEventSource.latest();

    emitEvent(source, event(29, 'FirstHalf', 29, 50));
    emitEvent(source, event(30, 'FirstHalf', 30, 0));
    emitEvent(source, event(31, 'FirstHalf', 30, 20));
    advance(0);

    expect(result.current.status).toBe('playing');
    expect(sequenceOf(result.current)).toBe(29);
    expect(result.current.clock).toEqual(clock('FirstHalf', 29, 50));

    advance(999);
    expect(sequenceOf(result.current)).toBe(29);
    advance(1);
    expect(sequenceOf(result.current)).toBe(30);

    advance(1999);
    expect(sequenceOf(result.current)).toBe(30);
    advance(1);
    expect(sequenceOf(result.current)).toBe(31);
    expect(result.current.match?.events.map((e) => e.sequence)).toEqual([29, 30, 31]);
  });

  it('plays at the speed it is given', () => {
    const { result } = replayOf(liveAt(), 60);
    act(() => result.current.start({ sequence: 30 }));
    const source = FakeEventSource.latest();

    emitEvent(source, event(29, 'FirstHalf', 29, 0));
    emitEvent(source, event(30, 'FirstHalf', 30, 0));
    advance(0);
    advance(999);
    expect(sequenceOf(result.current)).toBe(29);
    advance(1);
    expect(sequenceOf(result.current)).toBe(30);
  });

  it('starts from the score in the replay’s first state, not the live score', () => {
    const { result } = replayOf(liveAt());
    act(() => result.current.start({ sequence: 30 }));

    emitEvent(FakeEventSource.latest(), event(29, 'FirstHalf', 29), { home: 1, away: 0 });
    advance(0);

    expect(result.current.match?.state.score).toEqual({ home: 1, away: 0 });
    expect(result.current.match?.detail.summary.id).toBe('m1');
    expect(result.current.match?.fullTime).toBe(false);
  });

  it('starts with the moments from before the start point, and replays the rest as it passes them', () => {
    const { result } = replayOf(liveAt());
    act(() => result.current.start({ sequence: 30 }));
    const source = FakeEventSource.latest();

    emitEvent(source, event(29, 'FirstHalf', 29));
    emitEvent(source, event(30, 'FirstHalf', 30));
    act(() => source.emit('moment', moment(30), 30));
    advance(0);
    expect(Object.keys(result.current.match?.moments ?? {})).toEqual(['m1:10']);

    advance(6000);
    expect(Object.keys(result.current.match?.moments ?? {})).toEqual(['m1:10', 'm1:30']);
  });

  it('plays shapes and overlay cards through the live reducer as the playhead passes them', () => {
    const { result } = replayOf(liveAt());
    act(() => result.current.start({ sequence: 30 }));
    const source = FakeEventSource.latest();
    const shape = { sequence: 30, positions: {} } as unknown as ShapeView;
    const card = { id: 'goal-30', type: 'goal', at: clock('FirstHalf', 30) } as unknown as OverlayCard;

    emitEvent(source, event(29, 'FirstHalf', 29));
    emitEvent(source, event(30, 'FirstHalf', 30));
    act(() => {
      source.emit('shape', shape, 30);
      source.emit('overlay', card, 30);
    });
    advance(0);
    expect(result.current.match?.overlays).toEqual([]);
    expect(result.current.match?.shape).toBeNull();

    advance(6000);
    expect(result.current.match?.overlays).toEqual([card]);
    expect(result.current.match?.shape).toEqual(shape);
  });

  it('pauses at the first half’s end for max(15 min / speed, 15 s)', () => {
    expect(halfTimePauseMs(10)).toBe(90_000);
    expect(halfTimePauseMs(100)).toBe(15_000);

    const { result } = replayOf(liveAt());
    act(() => result.current.start({ period: 'FirstHalf', minute: 47 }));
    const source = FakeEventSource.latest();

    emitEvent(source, event(60, 'FirstHalf', 47, 0));
    emitEvent(source, event(61, 'FirstHalf', 47, 10, 'PeriodEnd'));
    emitEvent(source, event(62, 'SecondHalf', 45, 0, 'KickOff'));
    advance(0);
    advance(1000);
    expect(sequenceOf(result.current)).toBe(61);

    advance(89_999);
    expect(sequenceOf(result.current)).toBe(61);
    advance(1);
    expect(sequenceOf(result.current)).toBe(62);
  });

  it('goes back to idle and closes its stream when the playhead reaches the live edge', () => {
    const { result } = replayOf(liveAt(31, clock('FirstHalf', 30, 20)));
    act(() => result.current.start({ sequence: 30 }));
    const source = FakeEventSource.latest();

    emitEvent(source, event(29, 'FirstHalf', 29, 50));
    emitEvent(source, event(30, 'FirstHalf', 30, 0));
    emitEvent(source, event(31, 'FirstHalf', 30, 20));
    advance(0);
    advance(1000);
    expect(result.current.status).toBe('playing');
    expect(result.current.behindLiveSeconds).toBe(20);

    advance(2000);
    expect(result.current.status).toBe('idle');
    expect(result.current.match).toBeNull();
    expect(source.closed).toBe(true);
  });

  it('catches up with a live edge that has moved on while it played', () => {
    const { result, rerender } = replayOf(liveAt(30, clock('FirstHalf', 30)));
    act(() => result.current.start({ sequence: 30 }));
    const source = FakeEventSource.latest();

    emitEvent(source, event(29, 'FirstHalf', 29, 50));
    emitEvent(source, event(30, 'FirstHalf', 30, 0));
    rerender({ current: liveAt(31, clock('FirstHalf', 30, 20)) });
    emitEvent(source, event(31, 'FirstHalf', 30, 20));
    advance(0);
    advance(1000);
    expect(result.current.status).toBe('playing');

    advance(2000);
    expect(result.current.status).toBe('idle');
  });

  it('plays a finished match to full time, then goes back to idle', () => {
    const { result } = replayOf(liveAt(200, clock('SecondHalf', 93), { fullTime: true }));
    act(() => result.current.start({ sequence: 199 }));
    const source = FakeEventSource.latest();

    emitEvent(source, event(198, 'SecondHalf', 92, 50));
    emitEvent(source, event(199, 'SecondHalf', 93, 0));
    emitEvent(source, event(200, 'SecondHalf', 93, 10, 'PeriodEnd'));
    act(() => source.emit('fulltime', state(200, clock('SecondHalf', 93, 10), { fullTime: true }), 200));
    expect(source.closed).toBe(true);
    advance(0);
    expect(result.current.status).toBe('playing');
    expect(sequenceOf(result.current)).toBe(198);

    advance(1000);
    expect(sequenceOf(result.current)).toBe(199);
    advance(1000);
    expect(result.current.status).toBe('idle');
  });

  it('back to live closes the stream, clears its timers and returns to idle', () => {
    const { result } = replayOf(liveAt());
    act(() => result.current.start({ sequence: 30 }));
    const source = FakeEventSource.latest();
    emitEvent(source, event(29, 'FirstHalf', 29));
    emitEvent(source, event(30, 'FirstHalf', 30));
    advance(0);

    act(() => result.current.backToLive());

    expect(source.closed).toBe(true);
    expect(result.current.status).toBe('idle');
    expect(result.current.match).toBeNull();
    expect(vi.getTimerCount()).toBe(0);
    advance(10_000);
    expect(result.current.status).toBe('idle');
  });

  it('a restart closes the previous stream and replaces it', () => {
    const { result } = replayOf(liveAt());
    act(() => result.current.start({ sequence: 30 }));
    const first = FakeEventSource.latest();
    emitEvent(first, event(29, 'FirstHalf', 29));
    emitEvent(first, event(30, 'FirstHalf', 30));
    advance(0);

    act(() => result.current.start({ sequence: 90 }));
    const second = FakeEventSource.latest();

    expect(first.closed).toBe(true);
    expect(second).not.toBe(first);
    expect(streamUrl(second).searchParams.get('after')).toBe('88');
    expect(result.current.status).toBe('loading');
    expect(result.current.match).toBeNull();

    advance(10_000);
    expect(result.current.status).toBe('loading');
    emitEvent(second, event(89, 'SecondHalf', 55), { home: 2, away: 0 });
    advance(0);
    expect(sequenceOf(result.current)).toBe(89);
    expect(result.current.match?.state.score).toEqual({ home: 2, away: 0 });
  });

  it('closes its stream on unmount', () => {
    const { result, unmount } = replayOf(liveAt());
    act(() => result.current.start({ sequence: 30 }));
    const source = FakeEventSource.latest();

    unmount();

    expect(source.closed).toBe(true);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('reports an error when the browser gives up on the stream', () => {
    const { result } = replayOf(liveAt());
    act(() => result.current.start({ sequence: 30 }));
    const source = FakeEventSource.latest();

    act(() => source.fail());

    expect(result.current.status).toBe('error');
    expect(source.closed).toBe(true);
  });
});

describe('secondsBehind', () => {
  it('is the clock difference within a half', () => {
    expect(secondsBehind(clock('SecondHalf', 60, 30), clock('SecondHalf', 52))).toBe(510);
  });

  it('counts the second half so far and the replay’s distance to 45:00 across half time', () => {
    expect(secondsBehind(clock('SecondHalf', 50), clock('FirstHalf', 40))).toBe(600);
    expect(secondsBehind(clock('SecondHalf', 46), clock('FirstHalf', 46))).toBe(60);
  });

  it('is never negative', () => {
    expect(secondsBehind(clock('FirstHalf', 10), clock('FirstHalf', 12))).toBe(0);
  });
});
