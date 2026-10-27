import { act, cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { OverlayCard } from '../live/matchStreamReducer';
import { GoalToast, goalToastFadeMs, type GoalToastProps } from './GoalToast';

const card = (id: string, durationMs = 4000): OverlayCard => ({
  id,
  at: { period: 'SecondHalf', minute: 68, second: 2, display: '68:02' },
  type: 'goal',
  durationMs,
  payload: { title: 'GOAL!', subtitle: null, team: 'Home', playerId: 'h9' },
});

const props = (over: Partial<GoalToastProps> = {}): GoalToastProps => ({
  card: card('g1'),
  scorerName: 'Ana Torres',
  teamName: 'North Bank',
  score: { home: 2, away: 1, homeName: 'North Bank', awayName: 'Tavern United' },
  minute: "68'",
  onDone: () => undefined,
  ...over,
});

describe('GoalToast', () => {
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('shows the scorer, team, score and minute', () => {
    render(<GoalToast {...props()} />);

    const toast = screen.getByTestId('goal-toast');
    expect(toast).toHaveAttribute('aria-hidden', 'true');
    expect(toast).toHaveTextContent('GOAL!');
    expect(toast).toHaveTextContent('Ana Torres');
    expect(toast).toHaveTextContent('North Bank 2 – 1 Tavern United');
    expect(toast).toHaveTextContent("68'");
    expect(toast).not.toHaveTextContent('Replay');
  });

  it('carries no team colour: no inline style, so the plain white text is all the CSS paints', () => {
    render(<GoalToast {...props()} />);

    expect(screen.getByTestId('goal-toast')).not.toHaveAttribute('style');
  });

  it('keeps one live region mounted, empty until a goal arrives, and announces the goal', () => {
    const { rerender } = render(<GoalToast {...props({ card: null })} />);
    const region = screen.getByRole('status', { name: 'Goal announcements' });
    expect(region).toBeEmptyDOMElement();
    expect(screen.queryByTestId('goal-toast')).not.toBeInTheDocument();

    rerender(<GoalToast {...props()} />);

    expect(screen.getByRole('status', { name: 'Goal announcements' })).toBe(region);
    expect(region).toHaveTextContent('Goal, North Bank, Ana Torres, 68 minutes, 2–1');
  });

  it('fades after the duration and calls onDone once the fade ends', () => {
    vi.useFakeTimers();
    const onDone = vi.fn();
    render(<GoalToast {...props({ onDone })} />);

    act(() => vi.advanceTimersByTime(3999));
    expect(screen.getByTestId('goal-toast')).not.toHaveClass('goal-toast-fading');
    act(() => vi.advanceTimersByTime(1));
    expect(screen.getByTestId('goal-toast')).toHaveClass('goal-toast-fading');
    expect(onDone).not.toHaveBeenCalled();

    act(() => vi.advanceTimersByTime(goalToastFadeMs));
    expect(onDone).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId('goal-toast')).not.toBeInTheDocument();
  });

  it('uses 4,000 ms when the card asks for less than 3,000', () => {
    vi.useFakeTimers();
    const onDone = vi.fn();
    render(<GoalToast {...props({ card: card('g1', 1000), onDone })} />);

    act(() => vi.advanceTimersByTime(3999 + goalToastFadeMs));
    expect(onDone).not.toHaveBeenCalled();
    act(() => vi.advanceTimersByTime(1));
    expect(onDone).toHaveBeenCalledTimes(1);
  });

  it('restarts the timer when a second goal card arrives', () => {
    vi.useFakeTimers();
    const onDone = vi.fn();
    const { rerender } = render(<GoalToast {...props({ onDone })} />);

    act(() => vi.advanceTimersByTime(3000));
    rerender(
      <GoalToast
        {...props({ card: card('g2'), scorerName: 'Mo Reyes', score: { ...props().score, home: 3 }, onDone })}
      />,
    );
    expect(screen.getByTestId('goal-toast')).toHaveTextContent('Mo Reyes');

    act(() => vi.advanceTimersByTime(3999));
    expect(screen.getByTestId('goal-toast')).not.toHaveClass('goal-toast-fading');
    expect(onDone).not.toHaveBeenCalled();

    act(() => vi.advanceTimersByTime(1 + goalToastFadeMs));
    expect(onDone).toHaveBeenCalledTimes(1);
  });

  it('shows a replayed card with the same id again', () => {
    vi.useFakeTimers();
    const onDone = vi.fn();
    const live = card('g1');
    const { rerender } = render(<GoalToast {...props({ card: live, onDone })} />);
    act(() => vi.advanceTimersByTime(4000 + goalToastFadeMs));
    expect(onDone).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId('goal-toast')).not.toBeInTheDocument();

    rerender(<GoalToast {...props({ card: { ...live }, replay: true, onDone })} />);
    expect(screen.getByTestId('goal-toast')).toHaveTextContent('Replay');
    act(() => vi.advanceTimersByTime(4000 + goalToastFadeMs));
    expect(onDone).toHaveBeenCalledTimes(2);
  });

  it('does not restart the timer when only onDone changes', () => {
    vi.useFakeTimers();
    const first = vi.fn();
    const second = vi.fn();
    const shown = card('g1');
    const { rerender } = render(<GoalToast {...props({ card: shown, onDone: first })} />);
    act(() => vi.advanceTimersByTime(3000));
    rerender(<GoalToast {...props({ card: shown, onDone: second })} />);

    act(() => vi.advanceTimersByTime(1000 + goalToastFadeMs));
    expect(first).not.toHaveBeenCalled();
    expect(second).toHaveBeenCalledTimes(1);
  });

  it('shows a Replay tag when replaying', () => {
    render(<GoalToast {...props({ replay: true })} />);
    expect(screen.getByTestId('goal-toast')).toHaveTextContent('Replay');
  });

  it('animates by default and only fades in when the visitor prefers reduced motion', () => {
    const { unmount } = render(<GoalToast {...props()} />);
    expect(screen.getByTestId('goal-toast')).toHaveClass('goal-toast-animated');
    unmount();

    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: query.includes('reduce'),
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    }));
    render(<GoalToast {...props()} />);
    const toast = screen.getByTestId('goal-toast');
    expect(toast).not.toHaveClass('goal-toast-animated');
    expect(toast).toHaveClass('goal-toast-still');
  });
});
