import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { describe, expect, it } from 'vitest';
import { NotFoundPage } from './NotFoundPage';

describe('NotFoundPage', () => {
  it('calls offside on an unknown path, with links back to the matches and the teams', () => {
    render(
      <MemoryRouter initialEntries={['/nowhere']}>
        <Routes>
          <Route path="*" element={<NotFoundPage />} />
        </Routes>
      </MemoryRouter>,
    );

    expect(screen.getByRole('heading', { level: 1, name: 'Offside!' })).toBeInTheDocument();
    expect(screen.getByText("That page isn't where it should be. The flag's up.")).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Matches' })).toHaveAttribute('href', '/');
    expect(screen.getByRole('link', { name: 'Teams' })).toHaveAttribute('href', '/teams');
  });
});
