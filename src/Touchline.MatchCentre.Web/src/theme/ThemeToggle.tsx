import { useEffect, useState } from 'react';
import { applyTheme, loadTheme, saveTheme, type Theme } from './theme';

const options: Array<{ theme: Theme; label: string }> = [
  { theme: 'dark', label: 'Dark' },
  { theme: 'light', label: 'Light' },
];

/**
 * A two-option switch, Dark | Light, with the current theme highlighted and a visible "Theme" label. The choice is
 * remembered.
 */
export function ThemeToggle() {
  const [theme, setTheme] = useState<Theme>(loadTheme);

  useEffect(() => {
    applyTheme(theme);
  }, [theme]);

  function choose(next: Theme) {
    if (next === theme) return;
    saveTheme(next);
    setTheme(next);
  }

  return (
    <div className="theme-toggle" role="group" aria-label="Theme">
      <span className="theme-toggle-label" aria-hidden="true">
        Theme
      </span>
      {options.map(({ theme: option, label }) => (
        <button
          key={option}
          type="button"
          className="theme-toggle-option"
          aria-pressed={theme === option}
          aria-label={`${label} theme`}
          onClick={() => choose(option)}
        >
          {label}
        </button>
      ))}
    </div>
  );
}
