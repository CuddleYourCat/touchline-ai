import { act, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  ballPathOf,
  hasBall,
  type MatchDetail,
  type MatchEvent,
  type MomentumReading,
  type OverlayCard,
  type ShapeView,
} from '../live/matchStreamReducer';
import { MomentumBars } from './MomentumBars';
import { OverlayToast, overlayFadeMs } from './OverlayToast';
import { Pitch, type PitchProps } from './Pitch';
import { ballTrail } from './pitchGeometry';

type Player = MatchDetail['homeLineup'][number];

function lineup(team: 'h' | 'a'): Player[] {
  return Array.from({ length: 11 }, (_, i) => ({
    id: `${team}${i + 1}`,
    name: `Player ${team}${i + 1}`,
    shirtNumber: i + 1,
    position: 'Midfielder',
    teamId: team,
    archetype: null,
    preferredFoot: 'Right',
  })) as Player[];
}

function shape(sequence: number, shift = 0): ShapeView {
  const positions = (['Home', 'Away'] as const).flatMap((team) =>
    Array.from({ length: 11 }, (_, i) => ({
      playerId: `${team === 'Home' ? 'h' : 'a'}${i + 1}`,
      team,
      x: (team === 'Home' ? 20 : 70) + i * 2 + shift,
      y: 5 + i * 5,
    })),
  );
  return { sequence, positions };
}

function event(sequence: number, overrides: Partial<MatchEvent> = {}): MatchEvent {
  return {
    sequence,
    matchId: 'm1',
    possessionId: 1,
    time: { period: 'FirstHalf', clock: '10:00' },
    type: 'Pass',
    team: 'Home',
    playerId: 'h4',
    start: { x: 40, y: 30 },
    end: { x: 60, y: 40 },
    ...overrides,
  } as MatchEvent;
}

function renderPitch(props: Partial<PitchProps> = {}) {
  return render(
    <Pitch shape={null} ballPath={null} events={[]} homeLineup={lineup('h')} awayLineup={lineup('a')} {...props} />,
  );
}

describe('Pitch', () => {
  it('draws no dots and no ball before the first shape and event, and still shows the caption', () => {
    renderPitch();

    expect(screen.queryAllByTestId('player-dot')).toHaveLength(0);
    expect(screen.queryByTestId('ball')).not.toBeInTheDocument();
    expect(screen.getByText('Modelled positions, not tracking data')).toBeInTheDocument();
  });

  it('draws 22 dots with shirt numbers, and moves them to a later shape', () => {
    const { rerender } = renderPitch({ shape: shape(10) });

    const dots = screen.getAllByTestId('player-dot');
    expect(dots).toHaveLength(22);
    expect(dots.filter((d) => d.classList.contains('player-home'))).toHaveLength(11);
    const first = dots[0]!;
    expect(first).toHaveTextContent('1');
    expect(first.querySelector('title')).toHaveTextContent('1 Player h1');
    expect(first.style.transform).toBe('translate(20px, 63px)');

    rerender(
      <Pitch shape={shape(11, 5)} ballPath={null} events={[]} homeLineup={lineup('h')} awayLineup={lineup('a')} />,
    );

    expect(screen.getAllByTestId('player-dot')[0]!.style.transform).toBe('translate(25px, 63px)');
  });

  it('labels a player missing from the line-up by the number in their ID, or ?, and never shows the ID', () => {
    renderPitch({
      shape: {
        sequence: 1,
        positions: [
          { playerId: 'nbfc-07', team: 'Away', x: 50, y: 34 },
          { playerId: 'guest-star', team: 'Away', x: 60, y: 34 },
        ],
      },
    });

    const [numbered, unnumbered] = screen.getAllByTestId('player-dot');
    expect(numbered).toHaveTextContent('7');
    expect(unnumbered).toHaveTextContent('?');
    expect(numbered!.querySelector('title')).toHaveTextContent('Player not in the line-up');
    expect(screen.queryByText(/nbfc-07|guest-star/)).not.toBeInTheDocument();
  });

  it("labels a substitute from the bench by his own number and name, in the departing player's place", () => {
    const sub: Player = { ...lineup('h')[0]!, id: 'h12', name: 'Tavi Rookwell', shirtNumber: 12, position: 'Forward' };
    const before = shape(30);
    const after: ShapeView = {
      sequence: 31,
      positions: before.positions.map((p) => (p.playerId === 'h7' ? { ...p, playerId: 'h12' } : p)),
    };

    renderPitch({ shape: after, homeBench: [sub] });

    const dots = screen.getAllByTestId('player-dot');
    expect(dots).toHaveLength(22);
    const dot = dots.find((d) => d.getAttribute('data-player') === 'h12')!;
    expect(dot).toHaveTextContent('12');
    expect(dot.querySelector('title')).toHaveTextContent('12 Tavi Rookwell');
    expect(dot.style.transform).toBe(dots[6]!.style.transform);
    expect(dots.find((d) => d.getAttribute('data-player') === 'h7')).toBeUndefined();
  });

  it('leaves the ball and its trail alone for a substitution, which happens on the touchline', () => {
    const carry = event(5, { type: 'Carry', start: { x: 30, y: 20 }, end: { x: 40, y: 20 } });
    const pass = event(6, { start: { x: 40, y: 20 }, end: { x: 50, y: 30 } });
    const substitution = event(7, { type: 'Substitution', start: { x: 52.5, y: 0 }, end: null, playerId: 'h7', replacementPlayerId: 'h12' });

    expect(hasBall(substitution)).toBe(false);
    expect(hasBall(pass)).toBe(true);
    expect(ballTrail([carry, pass, substitution])).toEqual([{ x: 40, y: 20 }]);
  });

  describe('sends a shot where its outcome says', () => {
    const shot = (outcome: 'Goal' | 'Saved' | 'Blocked' | 'OffTarget', end: MatchEvent['end'], start = { x: 90, y: 30 }) =>
      ballPathOf(event(9, { type: 'Shot', start, end, shot: { outcome } as MatchEvent['shot'] })).to;

    it('a goal to the goal centre, or its end', () => {
      expect(shot('Goal', null)).toEqual({ x: 105, y: 34 });
      expect(shot('Goal', { x: 105, y: 36 })).toEqual({ x: 105, y: 36 });
    });

    it('a save to the goal line, between the posts', () => {
      expect(shot('Saved', null)).toEqual({ x: 105, y: 34 });
      expect(shot('Saved', { x: 103, y: 50 })).toEqual({ x: 105, y: 37.66 });
    });

    it('a block to its end, or about 2 m on towards goal', () => {
      expect(shot('Blocked', { x: 93, y: 31 })).toEqual({ x: 93, y: 31 });
      expect(shot('Blocked', null, { x: 90, y: 34 })).toEqual({ x: 92, y: 34 });
    });

    it('a miss to its end, or the goal line wide of the near post', () => {
      expect(shot('OffTarget', { x: 105, y: 45 })).toEqual({ x: 105, y: 45 });
      expect(shot('OffTarget', null)).toEqual({ x: 105, y: 34 - 5.66 });
    });
  });

  it('animates a live ball, but draws a replayed one still', () => {
    const pass = event(10);
    const { rerender } = renderPitch({ shape: shape(10), ballPath: ballPathOf(pass), events: [pass] });
    expect(screen.getByTestId('ball').querySelector('animateMotion')).not.toBeNull();

    rerender(<Pitch shape={shape(20)} ballPath={ballPathOf(pass)} events={[pass]} homeLineup={[]} awayLineup={[]} />);

    const ball = screen.getByTestId('ball');
    expect(ball.querySelector('animateMotion')).toBeNull();
    expect(ball).toHaveAttribute('cx', '60');
  });

  it('drops markers more than 5 match minutes before the latest event, across half time too', () => {
    const at = (period: 'FirstHalf' | 'SecondHalf', clock: string) => ({ time: { period, clock } });
    const { rerender } = renderPitch({
      events: [
        event(1, { type: 'Foul', end: null, ...at('FirstHalf', '10:00') }),
        event(2, { type: 'Card', card: 'Yellow', end: null, ...at('FirstHalf', '14:00') }),
        event(3, at('FirstHalf', '16:00')),
      ],
    });
    expect(screen.queryByTestId('marker-foul')).not.toBeInTheDocument();
    expect(screen.getByTestId('marker-yellow')).toBeInTheDocument();

    const stoppage = event(4, { type: 'Foul', end: null, ...at('FirstHalf', '46:30') });
    rerender(<Pitch shape={null} ballPath={null} events={[stoppage, event(5, at('SecondHalf', '49:00'))]} homeLineup={[]} awayLineup={[]} />);
    expect(screen.getByTestId('marker-foul')).toBeInTheDocument();

    rerender(<Pitch shape={null} ballPath={null} events={[stoppage, event(6, at('SecondHalf', '51:00'))]} homeLineup={[]} awayLineup={[]} />);
    expect(screen.queryByTestId('marker-foul')).not.toBeInTheDocument();
  });

  it('arcs a pass, runs a carry straight and sends a shot with no end to goal', () => {
    const pass = event(5);
    const { rerender } = renderPitch({ ballPath: ballPathOf(pass), events: [pass] });

    const path = screen.getByTestId('ball-path');
    expect(path).toHaveAttribute('data-kind', 'pass');
    expect(path.getAttribute('d')).toMatch(/^M 40 38 Q .+ 60 28$/);
    expect(screen.getByTestId('ball')).toBeInTheDocument();

    const carry = event(6, { type: 'Carry' });
    rerender(<Pitch shape={null} ballPath={ballPathOf(carry)} events={[pass, carry]} homeLineup={[]} awayLineup={[]} />);
    expect(screen.getByTestId('ball-path')).toHaveAttribute('d', 'M 40 38 L 60 28');

    const shot = event(7, { type: 'Shot', team: 'Away', end: null });
    rerender(<Pitch shape={null} ballPath={ballPathOf(shot)} events={[pass, carry, shot]} homeLineup={[]} awayLineup={[]} />);
    expect(screen.getByTestId('ball-path')).toHaveAttribute('d', 'M 40 38 L 0 34');
    expect(screen.getAllByTestId('ball-trail-layer').map((l) => l.getAttribute('d'))).toEqual(['M 60 28 L 40 38']);
  });

  it('draws the trail as one joined line through the recent positions, strongest at the newest end', () => {
    const ends = [
      { x: 10, y: 10 },
      { x: 20, y: 20 },
      { x: 30, y: 10 },
      { x: 40, y: 20 },
    ];
    const events = ends.map((end, i) => event(i + 1, { type: 'Carry', start: ends[i - 1] ?? { x: 0, y: 10 }, end }));
    const latest = events.at(-1)!;

    renderPitch({ events, ballPath: ballPathOf(latest) });

    const layers = screen.getAllByTestId('ball-trail-layer').map((l) => l.getAttribute('d'));
    expect(screen.getAllByTestId('ball-trail')).toHaveLength(1);
    expect(layers).toEqual(['M 10 58 L 20 48 L 30 58', 'M 20 48 L 30 58']);
  });

  it('draws no trail before the ball has moved between two places', () => {
    const pass = event(1);

    renderPitch({ ballPath: ballPathOf(pass), events: [pass] });

    expect(screen.queryByTestId('ball-trail')).not.toBeInTheDocument();
  });

  it('shows a key to the foul and card markers under the pitch', () => {
    renderPitch();

    const key = screen.getByRole('list', { name: 'Pitch key' });
    expect(key).toHaveTextContent('Foul');
    expect(key).toHaveTextContent('Yellow card');
    expect(key).toHaveTextContent('Red card');
    expect(key.querySelector('.marker-yellow')).not.toBeNull();
    expect(key.querySelector('.marker-red')).not.toBeNull();
  });

  it('draws home as circles and away as diamonds, and says so in the key, so colour is not the only cue', () => {
    renderPitch({ shape: shape(10) });

    const dots = screen.getAllByTestId('player-dot');
    const home = dots.find((d) => d.classList.contains('player-home'))!;
    const away = dots.find((d) => d.classList.contains('player-away'))!;
    expect(home.querySelector('circle')).not.toBeNull();
    expect(home.querySelector('polygon')).toBeNull();
    expect(away.querySelector('polygon')).not.toBeNull();
    expect(away.querySelector('circle')).toBeNull();
    const key = screen.getByRole('list', { name: 'Pitch key' });
    expect(key.querySelector('.player-home circle')).not.toBeNull();
    expect(key.querySelector('.player-away polygon')).not.toBeNull();
  });

  it("dresses each side's tokens in its kit: shirt colour, trim rim and a readable number", () => {
    renderPitch({
      shape: shape(10),
      homeKit: { primary: '#d62828', secondary: '#ffffff' },
      awayKit: { primary: '#0b1f4f', secondary: '#ffe066' },
    });

    const figure = screen.getByRole('figure');
    expect(figure.style.getPropertyValue('--home-fill')).toBe('#d62828');
    expect(figure.style.getPropertyValue('--home-rim')).toBe('#ffffff');
    expect(figure.style.getPropertyValue('--away-fill')).toBe('#0b1f4f');
    expect(figure.style.getPropertyValue('--away-rim')).toBe('#ffe066');
    expect(figure.style.getPropertyValue('--away-ink')).toBe('#ffffff');
  });

  it('keeps the house colours for a side with no kit', () => {
    renderPitch({ shape: shape(10), awayKit: { primary: '#0b1f4f', secondary: '#ffe066' } });

    const figure = screen.getByRole('figure');
    expect(figure.style.getPropertyValue('--home-fill')).toBe('');
    expect(figure.style.getPropertyValue('--away-fill')).toBe('#0b1f4f');
  });

  it('marks fouls and cards', () => {
    const events = [event(1, { type: 'Foul', end: null }), event(2, { type: 'Card', card: 'Yellow', end: null }), event(3, { type: 'Card', card: 'Red', end: null })];

    renderPitch({ events, ballPath: ballPathOf(events[2]!) });

    expect(screen.getByTestId('marker-foul')).toBeInTheDocument();
    expect(screen.getByTestId('marker-yellow')).toBeInTheDocument();
    expect(screen.getByTestId('marker-red')).toBeInTheDocument();
    expect(screen.queryByTestId('ball-path')).not.toBeInTheDocument();
  });

  it('places the ball without animating when the visitor prefers reduced motion', () => {
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: query.includes('reduce'),
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    }));
    const pass = event(5);

    renderPitch({ ballPath: ballPathOf(pass), events: [pass] });

    const ball = screen.getByTestId('ball');
    expect(ball).toHaveAttribute('cx', '60');
    expect(ball.querySelector('animateMotion')).toBeNull();
    vi.unstubAllGlobals();
  });
});

describe('MomentumBars', () => {
  const reading = (period: MomentumReading['period'], fromMinute: number, value: number): MomentumReading => ({
    period,
    fromMinute,
    toMinute: fromMinute + 5,
    value,
  });

  it('draws one bar per window, home above the line and away below', () => {
    render(
      <MomentumBars
        readings={[reading('SecondHalf', 45, -0.5), reading('FirstHalf', 0, 0.8), reading('FirstHalf', 5, 0)]}
        homeName="Harbourside Rovers"
        awayName="Millbrook Athletic"
      />,
    );

    const bars = screen.getAllByTestId('momentum-bar');
    expect(bars).toHaveLength(3);
    expect(bars[0]).toHaveClass('momentum-home');
    expect(bars[0]).toHaveAttribute('y', '-16');
    expect(bars[0]).toHaveAttribute('height', '16');
    expect(bars[2]).toHaveClass('momentum-away');
    expect(bars[2]).toHaveAttribute('y', '0');
    expect(bars[2]).toHaveAttribute('height', '10');
  });

  it('shifts the second half past first-half added time, so the windows never overlap', () => {
    const { container } = render(
      <MomentumBars readings={[reading('SecondHalf', 45, -0.5), reading('FirstHalf', 45, 0.5)]} homeName="H" awayName="A" />,
    );

    const [first, second] = screen.getAllByTestId('momentum-bar');
    const firstEnd = Number(first!.getAttribute('x')) + Number(first!.getAttribute('width'));
    expect(first).toHaveAttribute('x', '45.4');
    expect(second).toHaveAttribute('x', '50.4');
    expect(firstEnd).toBeLessThan(Number(second!.getAttribute('x')));
    expect(screen.getByTestId('momentum-half')).toHaveAttribute('x1', '50');
    expect(container.querySelector('svg')).toHaveAttribute('viewBox', '0 -20 95 40');
  });

  it('says so when there are no readings yet', () => {
    render(<MomentumBars readings={[]} homeName="H" awayName="A" />);

    expect(screen.getByText('No momentum readings yet.')).toBeInTheDocument();
    expect(screen.queryAllByTestId('momentum-bar')).toHaveLength(0);
  });
});

describe('OverlayToast', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  const card = (id: string, title: string): OverlayCard => ({
    id,
    at: { period: 'FirstHalf', minute: 23, second: 4, display: '23:04' },
    type: 'goal',
    durationMs: 3000,
    payload: { title, subtitle: 'Ashby 1-0', team: 'Home', playerId: 'h9' },
  });

  it('shows the newest card for its duration, fades it, then removes it for good', () => {
    vi.useFakeTimers();
    const { rerender } = render(<OverlayToast overlays={[card('o1', 'GOAL')]} homeName="Harbourside Rovers" awayName="Millbrook" />);

    const toast = screen.getByTestId('overlay-toast');
    expect(toast).toHaveTextContent('GOAL');
    expect(toast).toHaveTextContent('Harbourside Rovers');
    expect(toast).not.toHaveClass('overlay-fading');

    act(() => vi.advanceTimersByTime(3000));
    expect(screen.getByTestId('overlay-toast')).toHaveClass('overlay-fading');

    act(() => vi.advanceTimersByTime(overlayFadeMs));
    expect(screen.queryByTestId('overlay-toast')).not.toBeInTheDocument();

    rerender(<OverlayToast overlays={[card('o1', 'GOAL')]} homeName="Harbourside Rovers" awayName="Millbrook" />);
    expect(screen.queryByTestId('overlay-toast')).not.toBeInTheDocument();

    rerender(<OverlayToast overlays={[card('o1', 'GOAL'), card('o2', 'YELLOW CARD')]} homeName="H" awayName="A" />);
    expect(screen.getByTestId('overlay-toast')).toHaveTextContent('YELLOW CARD');
  });

  it('keeps one live region mounted, empty until a card arrives, and fills it with the card', () => {
    const { rerender } = render(<OverlayToast overlays={[]} homeName="Harbourside Rovers" awayName="Millbrook" />);

    const region = screen.getByRole('status', { name: 'Overlay announcements' });
    expect(region).toBeEmptyDOMElement();

    rerender(<OverlayToast overlays={[card('o1', 'GOAL')]} homeName="Harbourside Rovers" awayName="Millbrook" />);

    expect(screen.getByRole('status', { name: 'Overlay announcements' })).toBe(region);
    expect(region).toHaveTextContent('23:04, GOAL, Ashby 1-0, Harbourside Rovers');
    expect(screen.getAllByRole('status')).toHaveLength(1);
    expect(screen.getByTestId('overlay-toast')).toHaveAttribute('aria-hidden', 'true');
  });
});
