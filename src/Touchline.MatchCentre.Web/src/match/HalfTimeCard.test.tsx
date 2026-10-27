import { render, screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { LiveState, MatchDetail, MomentumReading } from '../live/matchStreamReducer';
import { HalfTimeCard } from './HalfTimeCard';

type Stats = LiveState['home'];

function stats(overrides: Partial<Stats> = {}): Stats {
  return {
    goals: 0,
    shots: 0,
    shotsOnTarget: 0,
    expectedGoals: 0,
    passes: 0,
    passesCompleted: 0,
    fouls: 0,
    yellowCards: 0,
    redCards: 0,
    passAccuracy: 0,
    ...overrides,
  };
}

const detail = {
  summary: {
    home: { id: 'nb', name: 'North Bank', shortName: 'NBK', crestUrl: '' },
    away: { id: 'rv', name: 'Riverside', shortName: 'RVS', crestUrl: '' },
  },
} as unknown as MatchDetail;

function state(home: Partial<Stats> = {}, away: Partial<Stats> = {}): LiveState {
  return {
    matchId: 'm1',
    lastSequence: 100,
    clock: { period: 'FirstHalf', display: '45:00' },
    score: { home: 2, away: 1 },
    home: stats(home),
    away: stats(away),
    fullTime: false,
  } as unknown as LiveState;
}

function readings(values: number[]): MomentumReading[] {
  return values.map((value, i) => ({ period: 'FirstHalf', fromMinute: i * 5, toMinute: i * 5 + 5, value }));
}

function row(label: string): HTMLElement {
  return screen.getByRole('rowheader', { name: label }).closest('tr') as HTMLElement;
}

describe('HalfTimeCard', () => {
  it('shows the title, score and both team names', () => {
    render(<HalfTimeCard detail={detail} state={state()} momentum={[]} />);
    expect(screen.getByRole('region', { name: 'Half-time stats' })).toBeInTheDocument();
    expect(screen.getByText('Half time')).toBeInTheDocument();
    expect(screen.getByText('North Bank')).toBeInTheDocument();
    expect(screen.getByText('Riverside')).toBeInTheDocument();
    expect(screen.getByText('2-1')).toBeInTheDocument();
  });

  it('shows each stat row with home on the left and away on the right', () => {
    render(
      <HalfTimeCard
        detail={detail}
        state={state(
          { shots: 8, shotsOnTarget: 4, fouls: 5, yellowCards: 1 },
          { shots: 3, shotsOnTarget: 1, fouls: 7, yellowCards: 2 },
        )}
        momentum={[]}
      />,
    );
    const cells = (label: string) => within(row(label)).getAllByRole('cell').map((c) => c.textContent);
    expect(cells('Shots')).toEqual(['8', '3']);
    expect(cells('Shots on target')).toEqual(['4', '1']);
    expect(cells('Fouls')).toEqual(['5', '7']);
    expect(cells('Yellow cards')).toEqual(['1', '2']);
    expect(screen.getByRole('columnheader', { name: 'NBK' })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'RVS' })).toBeInTheDocument();
  });

  it('rounds xG to one decimal', () => {
    render(<HalfTimeCard detail={detail} state={state({ expectedGoals: 1.46 }, { expectedGoals: 0.04 })} momentum={[]} />);
    expect(within(row('xG')).getAllByRole('cell').map((c) => c.textContent)).toEqual(['1.5', '0.0']);
  });

  it('formats pass accuracy as a percentage, from a fraction or a percentage', () => {
    render(<HalfTimeCard detail={detail} state={state({ passAccuracy: 0.847 }, { passAccuracy: 72 })} momentum={[]} />);
    expect(within(row('Pass accuracy')).getAllByRole('cell').map((c) => c.textContent)).toEqual(['85%', '72%']);
  });

  it('hides the red-card row at 0-0 and shows it otherwise', () => {
    const { unmount } = render(<HalfTimeCard detail={detail} state={state()} momentum={[]} />);
    expect(screen.queryByRole('rowheader', { name: 'Red cards' })).not.toBeInTheDocument();
    unmount();
    render(<HalfTimeCard detail={detail} state={state({ redCards: 1 })} momentum={[]} />);
    expect(within(row('Red cards')).getAllByRole('cell').map((c) => c.textContent)).toEqual(['1', '0']);
  });

  it('says the home side had the better of the half when it led most readings', () => {
    render(<HalfTimeCard detail={detail} state={state()} momentum={readings([0.5, 0.3, 0.2, 0.4, -0.1, 0.6, 0.2, 0.1, 0.3])} />);
    expect(screen.getByText('North Bank had the better of the first half')).toBeInTheDocument();
  });

  it('says the away side had the better of the half when it led most readings', () => {
    render(<HalfTimeCard detail={detail} state={state()} momentum={readings([-0.5, -0.3, -0.2, -0.4, 0.1, -0.6, -0.2, -0.1, -0.3])} />);
    expect(screen.getByText('Riverside had the better of the first half')).toBeInTheDocument();
  });

  it('calls it even when neither side leads more than 60% of readings', () => {
    render(<HalfTimeCard detail={detail} state={state()} momentum={readings([0.5, -0.4, 0.2, -0.3, 0, 0.1])} />);
    expect(screen.getByText('An even first half')).toBeInTheDocument();
  });

  it('ignores second-half readings', () => {
    const second: MomentumReading[] = [-0.9, -0.8, -0.7, -0.6].map((value, i) => ({
      period: 'SecondHalf',
      fromMinute: 45 + i * 5,
      toMinute: 50 + i * 5,
      value,
    }));
    render(<HalfTimeCard detail={detail} state={state()} momentum={[...readings([0.5, 0.4, 0.3, 0.2]), ...second]} />);
    expect(screen.getByText('North Bank had the better of the first half')).toBeInTheDocument();
  });

  it('keeps the short team names readable by screen readers and the region keyboard-scrollable', () => {
    render(<HalfTimeCard detail={detail} state={state()} momentum={[]} />);
    expect(screen.getByText('NBK', { selector: '.half-time-short' })).not.toHaveAttribute('aria-hidden');
    expect(screen.getByRole('region', { name: 'Half-time stats' })).toHaveAttribute('tabindex', '0');
  });

  it('shows a Replay tag only for a replay', () => {
    const { rerender } = render(<HalfTimeCard detail={detail} state={state()} momentum={[]} />);
    expect(screen.queryByText('Replay')).not.toBeInTheDocument();
    rerender(<HalfTimeCard detail={detail} state={state()} momentum={[]} replay />);
    expect(screen.getByText('Replay')).toBeInTheDocument();
  });

  describe('at full time', () => {
    it('is titled and labelled for full time, in the page flow', () => {
      render(<HalfTimeCard detail={detail} state={state()} momentum={[]} phase="full" />);
      const card = screen.getByRole('region', { name: 'Full-time stats' });
      expect(card).toHaveClass('half-time-card-static');
      expect(screen.getByText('Full time')).toBeInTheDocument();
      expect(screen.queryByText('Half time')).not.toBeInTheDocument();
    });

    it('summarises every reading, not only the first half', () => {
      const second: MomentumReading[] = [-0.9, -0.8, -0.7, -0.6, -0.5, -0.4, -0.3].map((value, i) => ({
        period: 'SecondHalf',
        fromMinute: 45 + i * 5,
        toMinute: 50 + i * 5,
        value,
      }));
      render(<HalfTimeCard detail={detail} state={state()} momentum={[...readings([0.5, 0.4]), ...second]} phase="full" />);
      expect(screen.getByText('Riverside had the better of the match')).toBeInTheDocument();
    });

    it('says the match was even, and leaves the line out with no readings', () => {
      const { rerender } = render(<HalfTimeCard detail={detail} state={state()} momentum={readings([0.5, -0.5])} phase="full" />);
      expect(screen.getByText('An even match')).toBeInTheDocument();
      rerender(<HalfTimeCard detail={detail} state={state()} momentum={[]} phase="full" />);
      expect(screen.queryByText(/better of|even match/)).not.toBeInTheDocument();
    });
  });
});
