import { act, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { api } from '../api/client';
import { rememberEditKey } from '../teams/editKeys';
import { detail, fail, ok, options } from '../test/teamFixtures';
import { TeamEditPage } from './TeamEditPage';

const key = 'k7Qe3v9xZp2LmN8sT4wYb6Rj1Hc5Ud0Fa-_GqWiEoKs';
const teamId = 'fan-harbour-town';
const unavailable = { type: 'https://touchline.example/problems/name-check-unavailable', title: 'Names can not be checked' };
const message = "Names can't be checked right now. Nothing was saved; try again in a moment.";

function stubGets() {
  return vi.spyOn(api, 'GET').mockImplementation(((path: string) => {
    if (path === '/api/team-builder/options') return Promise.resolve(ok(options()));
    if (path === '/api/teams/{teamId}') return Promise.resolve(ok(detail(teamId, 3)));
    return Promise.resolve(fail(404));
  }) as never);
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/teams/new" element={<TeamEditPage />} />
        <Route path="/teams/:id/edit" element={<TeamEditPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

function type(label: string | RegExp, value: string) {
  fireEvent.change(screen.getByLabelText(label), { target: { value } });
}

describe('TeamEditPage: names that cannot be checked (503)', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
    localStorage.clear();
  });

  it('says nothing was saved when creating, and keeps the form', async () => {
    stubGets();
    vi.spyOn(api, 'POST').mockResolvedValue(fail(503, unavailable) as never);

    renderAt('/teams/new');
    await screen.findByRole('form', { name: 'New team' });
    type('Club name', 'Harbour Town');
    type('Short name', 'hbt');
    type('Ground', 'Quayside Park');
    type('Manager name', 'Rory Calloway');
    screen.getAllByLabelText(/^Player \d+ name$/).forEach((input, i) => fireEvent.change(input, { target: { value: `Player Number${i}` } }));
    fireEvent.click(screen.getByRole('button', { name: 'Create team' }));

    expect(await screen.findByText(message)).toBeInTheDocument();
    expect(screen.getByLabelText('Club name')).toHaveValue('Harbour Town');
  });

  it('says nothing was saved when updating, and keeps the form', async () => {
    rememberEditKey(teamId, key);
    stubGets();
    vi.spyOn(api, 'PUT').mockResolvedValue(fail(503, unavailable) as never);

    renderAt(`/teams/${teamId}/edit`);
    await screen.findByRole('form', { name: 'Edit team' });
    type('Club name', 'Harbour Town Albion');
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText(message)).toBeInTheDocument();
    expect(screen.getByLabelText('Club name')).toHaveValue('Harbour Town Albion');
  });

  it('shows the same calm note when a name check hits the 503', async () => {
    vi.useFakeTimers();
    stubGets();
    vi.spyOn(api, 'POST').mockResolvedValue(fail(503, unavailable) as never);

    renderAt('/teams/new');
    await act(() => vi.advanceTimersByTimeAsync(0));
    type('Club name', 'Harbour Town');
    fireEvent.blur(screen.getByLabelText('Club name'));
    await act(() => vi.advanceTimersByTimeAsync(1_500));

    expect(screen.getByText(message)).toBeInTheDocument();
    expect(screen.getByLabelText('Club name')).toHaveValue('Harbour Town');
  });
});
