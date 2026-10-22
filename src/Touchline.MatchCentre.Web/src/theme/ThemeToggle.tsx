import { useEffect, useState } from 'react';
import { applyTheme, loadTheme, saveTheme, type Theme } from './theme';

/** A button that flips between the dark and light themes and remembers the choice. */
export function ThemeToggle() {
  const [theme, setTheme] = useState<Theme>(loadTheme);

  useEffect(() => {
    applyTheme(theme);
  }, [theme]);

  const next: Theme = theme === 'dark' ? 'light' : 'dark';

  function toggle() {
    saveTheme(next);
    setTheme(next);
  }

  return (
    <button type="button" className="theme-toggle" onClick={toggle} aria-label={`Switch to ${next} theme`}>
      {next === 'light' ? 'Light' : 'Dark'}
    </button>
  );
}
