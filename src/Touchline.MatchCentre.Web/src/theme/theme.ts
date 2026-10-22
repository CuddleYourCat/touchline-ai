/** The two looks the Match Centre ships. Dark is the default. */
export type Theme = 'dark' | 'light';

/** The localStorage key the choice is kept under. index.html reads it too, before first paint. */
export const themeStorageKey = 'touchline.theme';

/**
 * The theme the visitor chose last time, or dark. The OS colour-scheme setting is ignored on
 * purpose (ADR-034). Storage can be blocked or throw (private windows, strict settings), so
 * every access is guarded.
 */
export function loadTheme(): Theme {
  try {
    return localStorage.getItem(themeStorageKey) === 'light' ? 'light' : 'dark';
  } catch {
    return 'dark';
  }
}

/** Puts the theme on `<html data-theme>`, where the CSS tokens pick it up. */
export function applyTheme(theme: Theme): void {
  document.documentElement.dataset.theme = theme;
}

/** Remembers the theme for the next visit, if storage allows. */
export function saveTheme(theme: Theme): void {
  try {
    localStorage.setItem(themeStorageKey, theme);
  } catch {
    // Storage blocked: the theme still applies for this visit.
  }
}
