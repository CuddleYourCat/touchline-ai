import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { api } from '../api/client';
import { rememberEditKey } from '../teams/editKeys';
import { fail, ok, summary } from '../test/teamFixtures';
import { TeamsPage } from './TeamsPage';

const key = 'k7Qe3v9xZp2LmN8sT4wYb6Rj1Hc5Ud0Fa-_GqWiEoKs';

function renderPage() {
  return render(
    <MemoryRouter>
      <TeamsPage />
    </MemoryRouter>,
  );
}

describe('TeamsPage', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    localStorage.clear();
  });

  it('lists built-in and fan-made clubs, with Edit only for teams this browser holds a key for', async () => {
    rememberEditKey('fan-harbour-town', key);
    rememberEditKey('fan-deleted-club', key);
    vi.spyOn(api, 'GET').mockResolvedValue(
      ok([summary('north-bank-fc', 'North Bank', 'BuiltIn'), summary('fan-harbour-town', 'Harbour Town'), summary('fan-oak-lane', 'Oak Lane')]) as never,
    );

    const { container } = renderPage();

    const yours = await screen.findByRole('region', { name: 'Your teams' });
    expect(within(yours).getByText('Harbour Town')).toBeInTheDocument();
    expect(within(yours).queryByText('Oak Lane')).not.toBeInTheDocument();

    const builtIn = screen.getByRole('region', { name: 'Built-in clubs' });
    expect(within(builtIn).getByText('North Bank')).toBeInTheDocument();
    expect(within(builtIn).queryByText('fan-made')).not.toBeInTheDocument();
    expect(within(builtIn).queryByRole('link', { name: /Edit/ })).not.toBeInTheDocument();
    expect(within(builtIn).getByRole('link', { name: 'Play a match with North Bank' })).toHaveAttribute('href', '/kickoff?home=north-bank-fc');

    const fan = screen.getByRole('region', { name: 'Fan-made clubs' });
    expect(within(fan).getAllByText('fan-made')).toHaveLength(2);
    expect(within(fan).getByRole('link', { name: 'Edit Harbour Town' })).toHaveAttribute('href', '/teams/fan-harbour-town/edit');
    expect(within(fan).queryByRole('link', { name: 'Edit Oak Lane' })).not.toBeInTheDocument();

    expect(container.querySelectorAll('img')).toHaveLength(4);
    expect(builtIn.querySelector('img')).toHaveAttribute('src', '/api/teams/north-bank-fc/crest.svg');
    expect(screen.getByRole('link', { name: 'Build a team' })).toHaveAttribute('href', '/teams/new');
  });

  it('links each card to its club page, edged in the accent rather than a per-club colour', async () => {
    vi.spyOn(api, 'GET').mockResolvedValue(ok([summary('north-bank-fc', 'North Bank', 'BuiltIn')]) as never);

    renderPage();

    const link = await screen.findByRole('link', { name: 'North Bank' });
    expect(link).toHaveAttribute('href', '/teams/north-bank-fc');
    expect(link.closest('li')?.getAttribute('style')).toBeNull();
    expect(screen.getByRole('link', { name: 'Play a match with North Bank' })).toBeInTheDocument();
    expect(link.closest('a')?.querySelector('a')).toBeNull();
  });

  it('links the empty fan-made message to the builder', async () => {
    vi.spyOn(api, 'GET').mockResolvedValue(ok([summary('north-bank-fc', 'North Bank', 'BuiltIn')]) as never);

    renderPage();

    const fan = await screen.findByRole('region', { name: 'Fan-made clubs' });
    expect(within(fan).getByRole('link', { name: 'Build the first one' })).toHaveAttribute('href', '/teams/new');
  });

  it('never puts an edit key in a link', async () => {
    rememberEditKey('fan-harbour-town', key);
    vi.spyOn(api, 'GET').mockResolvedValue(ok([summary('fan-harbour-town', 'Harbour Town')]) as never);

    const { container } = renderPage();
    await screen.findByRole('region', { name: 'Your teams' });

    expect(container.innerHTML).not.toContain(key);
  });

  it('shows a retry when the list fails, with no server text', async () => {
    const get = vi.spyOn(api, 'GET').mockResolvedValue(fail(503) as never);

    renderPage();

    expect(await screen.findByRole('alert')).toHaveTextContent("We couldn't load the clubs just now.");
    expect(screen.queryByText(/secret/)).not.toBeInTheDocument();

    get.mockResolvedValue(ok([summary('north-bank-fc', 'North Bank', 'BuiltIn')]) as never);
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    await waitFor(() => expect(screen.getByText('North Bank')).toBeInTheDocument());
  });
});
