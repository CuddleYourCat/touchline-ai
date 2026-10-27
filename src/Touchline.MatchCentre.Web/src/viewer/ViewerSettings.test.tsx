import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Viewer } from './viewer';
import { ViewerSettings } from './ViewerSettings';

const viewer: Viewer = { mode: 'Casual', language: 'en' };

function renderSettings(onChange: (next: Viewer) => void = () => {}) {
  return render(
    <>
      <ViewerSettings viewer={viewer} onChange={onChange} showPitchOption />
      <button type="button">Elsewhere on the page</button>
    </>,
  );
}

describe('ViewerSettings', () => {
  afterEach(() => {
    localStorage.clear();
  });

  it('is a gear button with a name, collapsed until it is clicked', async () => {
    const user = userEvent.setup();
    renderSettings();

    const gear = screen.getByRole('button', { name: 'Settings' });
    expect(gear).toHaveAttribute('aria-expanded', 'false');
    expect(gear).toHaveAttribute('aria-controls');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

    await user.click(gear);

    expect(gear).toHaveAttribute('aria-expanded', 'true');
    const popover = screen.getByRole('dialog', { name: 'Your view' });
    expect(popover).toHaveAttribute('id', gear.getAttribute('aria-controls'));
    expect(screen.getByRole('radio', { name: 'Casual' })).toBeChecked();
    expect(screen.getByRole('combobox', { name: 'Language' })).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'Grey pitch' })).toBeInTheDocument();
  });

  it('no longer offers a favourite club', async () => {
    const user = userEvent.setup();
    renderSettings();

    await user.click(screen.getByRole('button', { name: 'Settings' }));

    expect(screen.queryByRole('combobox', { name: 'Favourite club' })).not.toBeInTheDocument();
    expect(screen.getAllByRole('combobox')).toHaveLength(1);
  });

  it('reports a change of view and of language', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderSettings(onChange);
    await user.click(screen.getByRole('button', { name: 'Settings' }));

    await user.click(screen.getByRole('radio', { name: 'Analyst' }));
    expect(onChange).toHaveBeenLastCalledWith({ mode: 'Analyst', language: 'en' });

    await user.selectOptions(screen.getByRole('combobox', { name: 'Language' }), 'fr');
    expect(onChange).toHaveBeenLastCalledWith({ mode: 'Casual', language: 'fr' });
  });

  it('closes on Escape and hands focus back to the gear', async () => {
    const user = userEvent.setup();
    renderSettings();
    const gear = screen.getByRole('button', { name: 'Settings' });
    await user.click(gear);
    screen.getByRole('radio', { name: 'Analyst' }).focus();

    await user.keyboard('{Escape}');

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(gear).toHaveFocus();
    expect(gear).toHaveAttribute('aria-expanded', 'false');
  });

  it('closes on a click outside it, and stays open for a click inside', async () => {
    const user = userEvent.setup();
    renderSettings();
    await user.click(screen.getByRole('button', { name: 'Settings' }));

    await user.click(screen.getByRole('dialog', { name: 'Your view' }));
    expect(screen.getByRole('dialog')).toBeInTheDocument();

    await user.click(screen.getByText('Elsewhere on the page'));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('toggles from the gear, and closes when focus tabs out of it', async () => {
    const user = userEvent.setup();
    renderSettings();
    const gear = screen.getByRole('button', { name: 'Settings' });

    await user.click(gear);
    await user.click(gear);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

    await user.click(gear);
    screen.getByRole('checkbox', { name: 'Grey pitch' }).focus();
    await user.tab();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('leaves out the grey-pitch option where there is no pitch', async () => {
    const user = userEvent.setup();
    render(<ViewerSettings viewer={viewer} onChange={() => {}} />);

    await user.click(screen.getByRole('button', { name: 'Settings' }));

    expect(screen.queryByRole('checkbox', { name: 'Grey pitch' })).not.toBeInTheDocument();
  });
});
