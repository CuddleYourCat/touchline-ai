import { useCallback, useState } from 'react';
import type { components } from '../api/schema';

/** A viewing mode, in the contract's own names (ADR-027). */
export type ViewingMode = components['schemas']['ViewingMode'];

/**
 * Who is watching (ADR-027): the viewing mode, the language (a BCP 47 tag such as `es-MX`) and, optionally, a
 * favourite club's ID. The service stores no profile; the browser keeps this and sends it with every request that
 * produces words.
 */
export interface Viewer {
  mode: ViewingMode;
  language: string;
  /** The favourite club's ID, or undefined for none. */
  team?: string;
}

/** The viewing modes, in picker order. */
export const viewingModes: readonly { mode: ViewingMode; label: string; hint: string }[] = [
  { mode: 'Casual', label: 'Casual', hint: 'The story of the match, in plain words' },
  { mode: 'Analyst', label: 'Analyst', hint: 'Tactics, xG and why things happened' },
];

/** The languages the commentary is written in (ADR-045), and so the only ones the picker offers. */
export const languages: readonly { tag: string; label: string }[] = [
  { tag: 'en', label: 'English' },
  { tag: 'es', label: 'Español' },
  { tag: 'fr', label: 'Français' },
];

const supported = new Set(languages.map((l) => l.tag));

/**
 * The supported language for a tag: the tag's base language when it's one of {@link languages} (`es-MX` gives `es`),
 * otherwise English.
 */
export function supportedLanguage(tag: string | undefined | null): string {
  const base = normaliseLanguage(tag).split('-')[0] ?? 'en';
  return supported.has(base) ? base : 'en';
}

/** The localStorage key the viewer is kept under. */
export const viewerStorageKey = 'touchline.viewer';

/** The tag shape the server accepts (`Viewer.Create`): `en`, or `es-MX`. Anything else would be a 400. */
const languageTag = /^[a-z]{2,3}(-[A-Z]{2})?$/;

/**
 * A language tag the server will accept: the tag itself, its base language (`zh-Hans-CN` gives `zh`), or English.
 * The region is upper-cased first, so `en-us` becomes `en-US`.
 */
export function normaliseLanguage(tag: string | undefined | null): string {
  if (!tag) return 'en';
  const [base = '', region] = tag.split('-');
  const tidy = region && region.length === 2 ? `${base.toLowerCase()}-${region.toUpperCase()}` : tag;
  if (languageTag.test(tidy)) return tidy;
  return languageTag.test(base.toLowerCase()) ? base.toLowerCase() : 'en';
}

/** A casual viewer in the browser's language if it's supported (English otherwise), with no favourite club. */
export function defaultViewer(): Viewer {
  const language = typeof navigator !== 'undefined' ? navigator.language : undefined;
  return { mode: 'Casual', language: supportedLanguage(language) };
}

/** Club IDs are slugs (`north-bank-fc`); anything else in storage is ignored rather than sent. */
const clubId = /^[a-z0-9-]{1,64}$/;

/**
 * The viewer saved last time, or the default. Storage can be blocked, throw or hold anything (it's the visitor's
 * own), so every field is checked and a bad one falls back.
 */
export function loadViewer(): Viewer {
  const fallback = defaultViewer();
  let saved: unknown;
  try {
    const raw = localStorage.getItem(viewerStorageKey);
    saved = raw ? JSON.parse(raw) : undefined;
  } catch {
    return fallback;
  }
  if (typeof saved !== 'object' || saved === null) return fallback;
  const { mode, language, team } = saved as Record<string, unknown>;
  return {
    mode: mode === 'Analyst' || mode === 'Casual' ? mode : fallback.mode,
    language: typeof language === 'string' ? supportedLanguage(language) : fallback.language,
    ...(typeof team === 'string' && clubId.test(team) ? { team } : {}),
  };
}

/** Remembers the viewer for the next visit, if storage allows. */
export function saveViewer(viewer: Viewer): void {
  try {
    localStorage.setItem(viewerStorageKey, JSON.stringify(viewer));
  } catch {
    // Storage blocked: the choice still applies for this visit.
  }
}

/** The viewer as query parameters, in the contract's names. `team` is left out when there's no favourite. */
export function viewerQuery(viewer: Viewer): { mode: ViewingMode; lang: string; team?: string } {
  return viewer.team ? { mode: viewer.mode, lang: viewer.language, team: viewer.team } : { mode: viewer.mode, lang: viewer.language };
}

/**
 * The viewer for one match: the favourite club is kept only when it's playing. A club that isn't in this match adds
 * nothing to its words, and one that's since been deleted would make the server refuse every request with a 400.
 */
export function viewerForMatch(viewer: Viewer, clubIds: readonly string[]): Viewer {
  if (!viewer.team || clubIds.includes(viewer.team)) return viewer;
  return { mode: viewer.mode, language: viewer.language };
}

/** The saved viewer, and a setter that saves each change. */
export function useViewer(): [Viewer, (next: Viewer) => void] {
  const [viewer, setViewer] = useState<Viewer>(loadViewer);
  const update = useCallback((next: Viewer) => {
    saveViewer(next);
    setViewer(next);
  }, []);
  return [viewer, update];
}
