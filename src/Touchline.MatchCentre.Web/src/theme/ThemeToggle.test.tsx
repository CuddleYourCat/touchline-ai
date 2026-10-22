import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ThemeToggle } from './ThemeToggle';
import { themeStorageKey } from './theme';

describe('ThemeToggle', () => {
  beforeEach(() => {
    localStorage.clear();
    delete document.documentElement.dataset.theme;
  });

  afterEach(() => {
    localStorage.clear();
  });

  it('starts dark when nothing was saved, whatever the OS prefers', () => {
    vi.stubGlobal('matchMedia', (query: string) => ({ matches: query.includes('light'), media: query }));

    render(<ThemeToggle />);

    expect(document.documentElement.dataset.theme).toBe('dark');
    expect(screen.getByRole('button', { name: 'Switch to light theme' })).toBeInTheDocument();
  });

  it('switches to light, sets data-theme on html and remembers the choice', async () => {
    render(<ThemeToggle />);

    await userEvent.click(screen.getByRole('button', { name: 'Switch to light theme' }));

    expect(document.documentElement.dataset.theme).toBe('light');
    expect(localStorage.getItem(themeStorageKey)).toBe('light');
    expect(screen.getByRole('button', { name: 'Switch to dark theme' })).toBeInTheDocument();
  });

  it('restores a saved light theme', () => {
    localStorage.setItem(themeStorageKey, 'light');

    render(<ThemeToggle />);

    expect(document.documentElement.dataset.theme).toBe('light');
  });

  it('still toggles when storage throws', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });

    render(<ThemeToggle />);
    await userEvent.click(screen.getByRole('button', { name: 'Switch to light theme' }));

    expect(document.documentElement.dataset.theme).toBe('light');
  });
});
