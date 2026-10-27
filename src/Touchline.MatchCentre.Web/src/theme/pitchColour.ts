import { useEffect, useState } from 'react';

/** The pitch's colour: green, or grey for viewers who can't tell the red and blue players from the grass. */
export type PitchColour = 'green' | 'grey';

/** The localStorage key the choice is kept under. */
export const pitchStorageKey = 'touchline.pitch';

/**
 * The saved pitch colour. With nothing saved, a browser that asks for more contrast (`prefers-contrast: more`) gets
 * grey and everyone else gets green; a saved choice always wins. Storage and `matchMedia` can be missing or throw, so
 * both are guarded.
 */
export function loadPitchColour(): PitchColour {
  try {
    const saved = localStorage.getItem(pitchStorageKey);
    if (saved === 'grey' || saved === 'green') return saved;
  } catch {
    // Storage blocked: fall through to the browser's preference.
  }
  try {
    return window.matchMedia?.('(prefers-contrast: more)').matches ? 'grey' : 'green';
  } catch {
    return 'green';
  }
}

function hasSavedChoice(): boolean {
  try {
    const saved = localStorage.getItem(pitchStorageKey);
    return saved === 'grey' || saved === 'green';
  } catch {
    return false;
  }
}

/** Puts the colour on `<html data-pitch>`, where the CSS overrides the pitch tokens. The mowing stripes stay. */
export function applyPitchColour(colour: PitchColour): void {
  document.documentElement.dataset.pitch = colour;
}

/** Remembers the colour for the next visit, if storage allows. */
export function savePitchColour(colour: PitchColour): void {
  try {
    localStorage.setItem(pitchStorageKey, colour);
  } catch {
    // Storage blocked: the choice still applies for this visit.
  }
}

/** The saved pitch colour, applied to the page, and a setter that saves each change. */
export function usePitchColour(): [PitchColour, (next: PitchColour) => void] {
  const [colour, setColour] = useState<PitchColour>(loadPitchColour);

  useEffect(() => {
    applyPitchColour(colour);
  }, [colour]);

  // While nothing is saved, follow the browser's contrast preference if it changes; a saved choice always wins.
  useEffect(() => {
    let query: MediaQueryList | undefined;
    try {
      query = window.matchMedia?.('(prefers-contrast: more)');
    } catch {
      return;
    }
    if (!query?.addEventListener) return;
    const onChange = (e: MediaQueryListEvent) => {
      if (!hasSavedChoice()) setColour(e.matches ? 'grey' : 'green');
    };
    query.addEventListener('change', onChange);
    return () => query?.removeEventListener('change', onChange);
  }, []);

  const update = (next: PitchColour) => {
    savePitchColour(next);
    setColour(next);
  };
  return [colour, update];
}
