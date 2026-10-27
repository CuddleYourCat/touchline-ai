import userEvent from '@testing-library/user-event';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { api } from '../api/client';
import { rememberEditKey } from '../teams/editKeys';
import { detail, fail, ok } from '../test/teamFixtures';
import { ClubPage } from './ClubPage';

const key = 'k7Qe3v9xZp2LmN8sT4wYb6Rj1Hc5Ud0Fa-_GqWiEoKs';

function renderPage(id = 'fan-harbour-town') {
  return render(
    <MemoryRouter initialEntries={[`/teams/${id}`]}>
      <Routes>
        <Route path="/teams/:id" element={<ClubPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('ClubPage', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    localStorage.clear();
  });

  it('shows the squad by position, the manager, the stadium and a back link', async () => {
    vi.spyOn(api, 'GET').mockResolvedValue(ok(detail()) as never);

    renderPage();

    expect(await screen.findByRole('heading', { level: 1, name: 'Harbour Town' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Back to Teams/ })).toHaveAttribute('href', '/teams');

    const keepers = screen.getByRole('region', { name: 'Goalkeepers' });
    expect(within(keepers).getAllByRole('listitem')).toHaveLength(2);
    expect(within(keepers).getAllByText('GK')).toHaveLength(2);
    expect(within(screen.getByRole('region', { name: 'Defenders' })).getAllByRole('listitem')).toHaveLength(5);
    expect(within(screen.getByRole('region', { name: 'Midfielders' })).getAllByRole('listitem')).toHaveLength(5);
    expect(within(screen.getByRole('region', { name: 'Forwards' })).getAllByRole('listitem')).toHaveLength(4);

    const facts = screen.getByRole('region', { name: 'Club details' });
    expect(within(facts).getByText('Rory Calloway')).toBeInTheDocument();
    expect(within(facts).getByText('Balanced')).toBeInTheDocument();
    expect(within(facts).getByText('Harbour Town Ground')).toBeInTheDocument();
    expect(within(facts).getByText('#123456 / #ffffff')).toBeInTheDocument();
    expect(within(facts).getByText('Fan-made')).toBeInTheDocument();
  });

  it('offers Play, and Edit only when this browser holds the key', async () => {
    vi.spyOn(api, 'GET').mockResolvedValue(ok(detail()) as never);

    const first = renderPage();
    expect(await screen.findByRole('link', { name: 'Play a match with Harbour Town' })).toHaveAttribute('href', '/kickoff?home=fan-harbour-town');
    expect(screen.queryByRole('link', { name: 'Edit Harbour Town' })).not.toBeInTheDocument();
    first.unmount();

    rememberEditKey('fan-harbour-town', key);
    const second = renderPage();
    expect(await screen.findByRole('link', { name: 'Edit Harbour Town' })).toHaveAttribute('href', '/teams/fan-harbour-town/edit');
    expect(second.container.innerHTML).not.toContain(key);
  });

  it('copes with a club that has no manager', async () => {
    vi.spyOn(api, 'GET').mockResolvedValue(ok({ ...detail(), manager: null }) as never);

    renderPage();

    const facts = await screen.findByRole('region', { name: 'Club details' });
    expect(within(facts).getByText('Not listed')).toBeInTheDocument();
  });

  it('says so when the club does not exist', async () => {
    vi.spyOn(api, 'GET').mockResolvedValue(fail(404) as never);

    renderPage('nope');

    expect(await screen.findByRole('heading', { name: 'Club not found' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Back to Teams/ })).toHaveAttribute('href', '/teams');
  });

  it('shows a retry when loading fails, with no server text', async () => {
    const get = vi.spyOn(api, 'GET').mockResolvedValue(fail(503) as never);

    renderPage();

    expect(await screen.findByRole('alert')).toHaveTextContent("We couldn't load this club just now.");
    expect(screen.queryByText(/secret/)).not.toBeInTheDocument();

    get.mockResolvedValue(ok(detail()) as never);
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    await waitFor(() => expect(screen.getByRole('heading', { level: 1, name: 'Harbour Town' })).toBeInTheDocument());
  });

  describe('player cards', () => {
    const stats = {
      appearances: 7, substituteAppearances: 1, goals: 4, assists: 2, shots: 12, shotsOnTarget: 6, expectedGoals: 3.2,
      passes: 100, passesCompleted: 80, fouls: 3, yellowCards: 1, redCards: 0, bannedRounds: [],
    };
    const summary = (id: string, season: string | null, kickOffUtc: string) => ({ id, season, kickOffUtc });

    /** The club, the matches list and a player's season stats, by path. */
    function stubApi(matches: unknown, season = vi.fn()) {
      vi.spyOn(api, 'GET').mockImplementation(((path: string, init?: unknown) => {
        if (path === '/api/matches') return Promise.resolve(matches);
        if (path === '/api/seasons/{season}/players/{playerId}') {
          season(init);
          return Promise.resolve(ok(stats));
        }
        return Promise.resolve(ok(detail()));
      }) as never);
      return season;
    }

    it('opens a player card from the name, with no match section, and gives focus back on close', async () => {
      const season = stubApi(ok([summary('a', '2025', '2026-01-01T00:00:00Z'), summary('b', '2026', '2026-03-01T00:00:00Z'), summary('c', null, '2026-05-01T00:00:00Z')]));
      renderPage();
      const user = userEvent.setup();
      const name = await screen.findByRole('button', { name: 'Arlo Testwell' });

      await user.click(name);

      const card = await screen.findByRole('dialog', { name: 'Arlo Testwell' });
      expect(card).toHaveTextContent('Goalkeeper · Harbour Town');
      expect(within(card).queryByText('This match')).not.toBeInTheDocument();
      expect(await within(card).findByText('Appearances')).toBeInTheDocument();
      expect(season).toHaveBeenCalledWith({ params: { path: { season: '2026', playerId: 'fan-harbour-town-p1' } }, signal: expect.anything() });

      await user.click(within(card).getByRole('button', { name: 'Close' }));
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
      expect(name).toHaveFocus();
    });

    it('says there are no season stats when no match has a season, as for fan clubs', async () => {
      stubApi(ok([summary('c', null, '2026-05-01T00:00:00Z')]));
      renderPage();

      await userEvent.setup().click(await screen.findByRole('button', { name: 'Arlo Testwell' }));

      const card = await screen.findByRole('dialog', { name: 'Arlo Testwell' });
      expect(card).toHaveTextContent('No season stats');
      expect(card).not.toHaveTextContent('This match');
    });
  });
});
