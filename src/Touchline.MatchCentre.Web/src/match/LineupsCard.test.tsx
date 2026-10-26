import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { MatchDetail, MomentView } from '../live/matchStreamReducer';
import { LineupsCard } from './LineupsCard';

type Player = MatchDetail['homeLineup'][number];

function squad(team: string, teamName: string, count: number, first: number): Player[] {
  return Array.from({ length: count }, (_, i) => ({
    id: `${team}${first + i}`,
    name: `${teamName} Player ${first + i}`,
    shirtNumber: first + i,
    position: i === 0 ? 'Goalkeeper' : i < 4 ? 'Defender' : i < 8 ? 'Midfielder' : 'Forward',
    teamId: team,
    archetype: null,
    preferredFoot: 'Right',
  })) as Player[];
}

const detail = {
  summary: { home: { id: 'h', name: 'Northfield Rovers' }, away: { id: 'a', name: 'Eastbrook Town' } },
  homeLineup: squad('h', 'Home', 11, 1),
  awayLineup: squad('a', 'Away', 11, 1),
  homeSubstitutes: squad('h', 'Home', 3, 12),
  awaySubstitutes: squad('a', 'Away', 3, 12),
} as unknown as MatchDetail;

function moment(sequence: number, kind: string, playerId: string, relatedPlayerId: string | null, minute: number) {
  return {
    id: `m:${sequence}`,
    tier: 'Key',
    kind,
    sequence,
    clock: { period: minute < 45 ? 'FirstHalf' : 'SecondHalf', minute, second: 0, display: `${minute}:00` },
    team: 'Home',
    playerId,
    reason: kind,
    commentary: null,
    relatedPlayerId,
  } as MomentView;
}

const moments = [moment(1, 'Goal', 'h9', 'h8', 67), moment(2, 'Substitution', 'h12', 'h11', 70)];

describe('LineupsCard', () => {
  it('shows both teams, starters and benches', () => {
    render(<LineupsCard detail={detail} moments={[]} onSelectPlayer={() => {}} />);

    expect(screen.getByRole('heading', { name: 'Northfield Rovers' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Eastbrook Town' })).toBeInTheDocument();
    expect(screen.getAllByRole('heading', { name: 'Substitutes' })).toHaveLength(2);
    expect(screen.getAllByRole('button')).toHaveLength(28);
    expect(screen.getByRole('button', { name: 'Away Player 14' })).toBeInTheDocument();
    const home = screen.getByRole('region', { name: 'Northfield Rovers line-up' });
    expect(within(home).getAllByText('GK')).toHaveLength(2);
  });

  it('shows a goal and an assist with their minutes', () => {
    render(<LineupsCard detail={detail} moments={moments} onSelectPlayer={() => {}} />);

    expect(screen.getByRole('img', { name: "Goal, 68'" })).toBeInTheDocument();
    expect(screen.getByRole('img', { name: "Assist, 68'" })).toBeInTheDocument();
  });

  it('shows a substitution on the starter and the bench player', () => {
    render(<LineupsCard detail={detail} moments={moments} onSelectPlayer={() => {}} />);

    const off = screen.getByRole('button', { name: 'Home Player 11' }).closest('li') as HTMLElement;
    const on = screen.getByRole('button', { name: 'Home Player 12' }).closest('li') as HTMLElement;
    expect(within(off).getByRole('img', { name: "Went off, 71'" })).toBeInTheDocument();
    expect(within(on).getByRole('img', { name: "Came on, 71'" })).toBeInTheDocument();
    expect(on).not.toHaveClass('lineups-row-unused');
    expect(screen.getByRole('button', { name: 'Home Player 13' }).closest('li')).toHaveClass('lineups-row-unused');
  });

  it('calls onSelectPlayer with the ID when a name is clicked', async () => {
    const onSelect = vi.fn();
    render(<LineupsCard detail={detail} moments={[]} onSelectPlayer={onSelect} />);

    await userEvent.click(screen.getByRole('button', { name: 'Away Player 3' }));

    expect(onSelect).toHaveBeenCalledWith('a3');
  });

  it('marks the selected player as pressed', () => {
    render(<LineupsCard detail={detail} moments={[]} onSelectPlayer={() => {}} selectedPlayerId="h5" />);

    expect(screen.getByRole('button', { name: 'Home Player 5' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Home Player 6' })).toHaveAttribute('aria-pressed', 'false');
  });
});
