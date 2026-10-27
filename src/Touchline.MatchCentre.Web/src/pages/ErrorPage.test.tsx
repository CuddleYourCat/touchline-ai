import { render, screen, within } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Layout } from '../Layout';
import { ErrorPage } from './ErrorPage';

function Boom(): never {
  throw new Error('kaboom in the render');
}

function renderBroken() {
  const router = createMemoryRouter(
    [
      {
        element: <Layout />,
        errorElement: <ErrorPage />,
        children: [
          {
            errorElement: <ErrorPage />,
            children: [{ path: '/', element: <Boom /> }],
          },
        ],
      },
    ],
    { initialEntries: ['/'] },
  );
  return render(<RouterProvider router={router} />);
}

describe('ErrorPage', () => {
  beforeEach(() => {
    // React and the router log a render error; the test expects one.
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('replaces the developer screen with a calm message, inside the normal header', () => {
    renderBroken();

    expect(screen.getByRole('heading', { name: 'Something went wrong' })).toBeInTheDocument();
    expect(screen.queryByText(/Hey developer/)).not.toBeInTheDocument();
    expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument();
    expect(within(screen.getByRole('alert')).getByRole('link', { name: 'Matches' })).toHaveAttribute('href', '/');
  });

  it('keeps the error message in a collapsed details element, in a dev build', () => {
    renderBroken();

    const details = screen.getByText('Error details').closest('details');
    expect(details).not.toHaveAttribute('open');
    expect(details).toHaveTextContent('kaboom in the render');
  });

  it('reloads the page on Try again', () => {
    const reload = vi.fn();
    vi.stubGlobal('location', { ...window.location, reload });
    renderBroken();

    screen.getByRole('button', { name: 'Try again' }).click();

    expect(reload).toHaveBeenCalledOnce();
    vi.unstubAllGlobals();
  });
});
