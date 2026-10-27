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
});
