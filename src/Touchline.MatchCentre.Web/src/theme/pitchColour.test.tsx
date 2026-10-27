import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ViewerSettings } from '../viewer/ViewerSettings';
import { pitchStorageKey } from './pitchColour';

const viewer = { mode: 'Casual', language: 'en' } as const;

describe('the grey-pitch option', () => {
  beforeEach(() => {
    localStorage.clear();
    delete document.documentElement.dataset.pitch;
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  it('is off by default, so the pitch stays green', () => {
    render(<ViewerSettings viewer={viewer} onChange={() => {}} clubs={[]} showPitchOption />);

    expect(screen.getByRole('checkbox', { name: 'Grey pitch' })).not.toBeChecked();
    expect(document.documentElement.dataset.pitch).toBe('green');
  });

  it('puts data-pitch=grey on html and remembers the choice', async () => {
    render(<ViewerSettings viewer={viewer} onChange={() => {}} clubs={[]} showPitchOption />);

    await userEvent.click(screen.getByRole('checkbox', { name: 'Grey pitch' }));

    expect(document.documentElement.dataset.pitch).toBe('grey');
    expect(localStorage.getItem(pitchStorageKey)).toBe('grey');
  });

  it('restores a saved grey pitch', () => {
    localStorage.setItem(pitchStorageKey, 'grey');

    render(<ViewerSettings viewer={viewer} onChange={() => {}} clubs={[]} showPitchOption />);

    expect(screen.getByRole('checkbox', { name: 'Grey pitch' })).toBeChecked();
    expect(document.documentElement.dataset.pitch).toBe('grey');
  });

  it('starts grey when the browser asks for more contrast and nothing was saved', () => {
    vi.stubGlobal('matchMedia', (query: string) => ({ matches: query === '(prefers-contrast: more)', media: query }));

    render(<ViewerSettings viewer={viewer} onChange={() => {}} clubs={[]} showPitchOption />);

    expect(screen.getByRole('checkbox', { name: 'Grey pitch' })).toBeChecked();
    expect(document.documentElement.dataset.pitch).toBe('grey');
  });

  it('lets a saved green choice beat the browser asking for more contrast', () => {
    vi.stubGlobal('matchMedia', (query: string) => ({ matches: true, media: query }));
    localStorage.setItem(pitchStorageKey, 'green');

    render(<ViewerSettings viewer={viewer} onChange={() => {}} clubs={[]} showPitchOption />);

    expect(screen.getByRole('checkbox', { name: 'Grey pitch' })).not.toBeChecked();
    expect(document.documentElement.dataset.pitch).toBe('green');
  });

  it('labels the option as an accessibility setting and describes why', () => {
    render(<ViewerSettings viewer={viewer} onChange={() => {}} clubs={[]} showPitchOption />);

    expect(screen.getByRole('group', { name: /Accessibility/ })).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'Grey pitch' })).toHaveAccessibleDescription(
      'Makes the players easier to pick out from the grass, including for colour-blind viewers.',
    );
  });

  it('follows the browser contrast preference while nothing is saved', () => {
    let listener: ((e: { matches: boolean }) => void) | undefined;
    const removeEventListener = vi.fn();
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: false,
      media: query,
      addEventListener: (_: string, l: (e: { matches: boolean }) => void) => (listener = l),
      removeEventListener,
    }));

    const { unmount } = render(<ViewerSettings viewer={viewer} onChange={() => {}} clubs={[]} showPitchOption />);
    expect(document.documentElement.dataset.pitch).toBe('green');

    act(() => listener?.({ matches: true }));
    expect(screen.getByRole('checkbox', { name: 'Grey pitch' })).toBeChecked();
    expect(document.documentElement.dataset.pitch).toBe('grey');

    unmount();
    expect(removeEventListener).toHaveBeenCalled();
  });

  it('ignores contrast preference changes once a choice is saved', () => {
    let listener: ((e: { matches: boolean }) => void) | undefined;
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: false,
      media: query,
      addEventListener: (_: string, l: (e: { matches: boolean }) => void) => (listener = l),
      removeEventListener: () => {},
    }));
    localStorage.setItem(pitchStorageKey, 'green');

    render(<ViewerSettings viewer={viewer} onChange={() => {}} clubs={[]} showPitchOption />);
    act(() => listener?.({ matches: true }));

    expect(document.documentElement.dataset.pitch).toBe('green');
  });

  it('still applies when storage throws', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });

    render(<ViewerSettings viewer={viewer} onChange={() => {}} clubs={[]} showPitchOption />);
    await userEvent.click(screen.getByRole('checkbox', { name: 'Grey pitch' }));

    expect(document.documentElement.dataset.pitch).toBe('grey');
  });
});
