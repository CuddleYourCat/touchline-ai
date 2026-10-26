import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { api } from '../api/client';
import type { MatchDetail } from '../live/matchStreamReducer';
import { PlayerFlyout, type PlayerFlyoutProps } from './PlayerFlyout';
import type { PlayerFact } from './playerFacts';

type PlayerSummary = MatchDetail['homeLineup'][number];
type Clock = PlayerFact['clock'];

const player = {
  id: 'p9',
  name: 'Ada Marlow',
  shirtNumber: 9,
  position: 'Forward',
  teamId: 't1',
  archetype: null,
} as PlayerSummary;

const clock = (period: string, minute: number) => ({ period, minute, second: 0 }) as unknown as Clock;

const facts: PlayerFact[] = [
  { kind: 'goal', momentId: 'm1', sequence: 1, clock: clock('FirstHalf', 11) },
  { kind: 'yellow', momentId: 'm2', sequence: 2, clock: clock('SecondHalf', 60) },
  { kind: 'subOff', momentId: 'm3', sequence: 3, clock: clock('SecondHalf', 74) },
];

const stats = {
  id: '2026:p9',
  season: '2026',
  playerId: 'p9',
  teamId: 't1',
  appearances: 7,
  rounds: [],
  goals: 4,
  assists: 2,
  shots: 15,
  shotsOnTarget: 8,
  expectedGoals: 3.46,
  passes: 40,
  passesCompleted: 30,
  fouls: 3,
  yellowCards: 1,
  redCards: 0,
  bannedRounds: [8],
  recentPenaltiesTaken: [],
  recentPenaltiesFaced: [],
  substituteAppearances: 2,
};

function setup(over: Partial<PlayerFlyoutProps> = {}) {
  const props: PlayerFlyoutProps = {
    player,
    teamName: 'North Bank',
    facts,
    season: '2026',
    onClose: vi.fn(),
    onSelectMoment: vi.fn(),
    ...over,
  };
  return { props, ...render(<PlayerFlyout {...props} />) };
}

const never = () => vi.spyOn(api, 'GET').mockReturnValue(new Promise(() => {}) as never);

describe('PlayerFlyout', () => {
  afterEach(() => vi.restoreAllMocks());

  it('shows the header and this match, and a fact click replays from its moment', () => {
    never();
    const { props } = setup();

    const dialog = screen.getByRole('dialog', { name: 'Ada Marlow' });
    expect(dialog).toHaveAttribute('aria-modal', 'true');
    expect(within(dialog).getByText('Forward · North Bank')).toBeInTheDocument();
    expect(within(dialog).getByText("12'")).toBeInTheDocument();
    expect(within(dialog).getByText("75'")).toBeInTheDocument();

    fireEvent.click(within(dialog).getByRole('button', { name: /Yellow card/ }));
    expect(props.onSelectMoment).toHaveBeenCalledWith('m2');
  });

  it('pluralises the tally and lists only non-zero counts', () => {
    never();
    const two: PlayerFact[] = [
      { kind: 'goal', momentId: 'a', sequence: 1, clock: clock('FirstHalf', 1) },
      { kind: 'goal', momentId: 'b', sequence: 2, clock: clock('FirstHalf', 2) },
      { kind: 'yellow', momentId: 'c', sequence: 3, clock: clock('FirstHalf', 3) },
    ];
    setup({ facts: two });
    expect(screen.getByText('2 goals · 1 yellow card')).toBeInTheDocument();
  });

  it('says Nothing yet when the player has no facts', () => {
    never();
    setup({ facts: undefined });
    expect(screen.getByText('Nothing yet')).toBeInTheDocument();
  });

  it('shows season stats after the fetch', async () => {
    const get = vi.spyOn(api, 'GET').mockResolvedValue({ data: stats, response: { status: 200 } } as never);
    setup();

    expect(screen.getByText(/Loading season stats/)).toBeInTheDocument();
    expect(await screen.findByText('3.5')).toBeInTheDocument();
    expect(screen.getByText('75%')).toBeInTheDocument();
    expect(screen.getByText('Banned round')).toBeInTheDocument();
    expect(get).toHaveBeenCalledWith(
      '/api/seasons/{season}/players/{playerId}',
      expect.objectContaining({ params: { path: { season: '2026', playerId: 'p9' } } }),
    );
  });

  it('says No season stats yet on a 404', async () => {
    vi.spyOn(api, 'GET').mockResolvedValue({ error: {}, response: { status: 404 } } as never);
    setup();
    expect(await screen.findByText('No season stats yet')).toBeInTheDocument();
  });

  it('offers a retry on another error', async () => {
    const get = vi
      .spyOn(api, 'GET')
      .mockResolvedValueOnce({ error: {}, response: { status: 503 } } as never)
      .mockResolvedValueOnce({ data: stats, response: { status: 200 } } as never);
    setup();

    fireEvent.click(await screen.findByRole('button', { name: 'Try again' }));
    await waitFor(() => expect(screen.getByText('Appearances')).toBeInTheDocument());
    expect(get).toHaveBeenCalledTimes(2);
  });

  it('makes no request without a season', () => {
    const get = vi.spyOn(api, 'GET');
    setup({ season: null });
    expect(screen.getByText(/kept for league fixtures only/)).toBeInTheDocument();
    expect(get).not.toHaveBeenCalled();
  });

  it('moves focus to the close button and closes on Escape', () => {
    never();
    const { props } = setup();
    expect(screen.getByRole('button', { name: 'Close' })).toHaveFocus();

    fireEvent.keyDown(document, { key: 'Escape' });
    expect(props.onClose).toHaveBeenCalledTimes(1);
  });

  it('returns focus to the opener when it closes', () => {
    never();
    const opener = document.createElement('button');
    document.body.append(opener);
    opener.focus();
    const { rerender, props } = setup();
    rerender(<PlayerFlyout {...props} player={null} />);
    expect(opener).toHaveFocus();
    opener.remove();
  });

  it('renders nothing when closed', () => {
    const { container } = setup({ player: null });
    expect(container).toBeEmptyDOMElement();
  });
});
