import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  defaultViewer,
  loadViewer,
  normaliseLanguage,
  saveViewer,
  supportedLanguage,
  viewerQuery,
  viewerStorageKey,
} from './viewer';

describe('viewer', () => {
  afterEach(() => {
    localStorage.clear();
    vi.restoreAllMocks();
  });

  it.each([
    ['es-MX', 'es-MX'],
    ['en-us', 'en-US'],
    ['FR', 'fr'],
    ['zh-Hans-CN', 'zh'],
    ['es-419', 'es'],
    ['', 'en'],
    [undefined, 'en'],
    ['not a tag', 'en'],
  ])('turns the language %j into %j, a tag the server accepts', (tag, expected) => {
    expect(normaliseLanguage(tag)).toBe(expected);
  });

  it.each([
    ['en', 'en'],
    ['es-MX', 'es'],
    ['FR', 'fr'],
    ['fr-CA', 'fr'],
    ['de-DE', 'en'],
    ['pt-BR', 'en'],
    ['zh-Hans-CN', 'en'],
    [undefined, 'en'],
  ])('supports the browser language %j as %j (English, Spanish and French only)', (tag, expected) => {
    expect(supportedLanguage(tag)).toBe(expected);
  });

  it('starts a new viewer in a supported browser language, or English', () => {
    const language = vi.spyOn(navigator, 'language', 'get');

    language.mockReturnValue('es-AR');
    expect(defaultViewer()).toEqual({ mode: 'Casual', language: 'es' });

    language.mockReturnValue('ja-JP');
    expect(defaultViewer()).toEqual({ mode: 'Casual', language: 'en' });
  });

  it('remembers the viewer and reads it back', () => {
    saveViewer({ mode: 'Analyst', language: 'fr' });

    expect(loadViewer()).toEqual({ mode: 'Analyst', language: 'fr' });
  });

  it('reads a saved language the picker no longer offers as English', () => {
    localStorage.setItem(viewerStorageKey, JSON.stringify({ mode: 'Casual', language: 'pt-BR' }));

    expect(loadViewer().language).toBe('en');
  });

  it('falls back field by field when storage holds something odd', () => {
    vi.spyOn(navigator, 'language', 'get').mockReturnValue('fr-FR');
    localStorage.setItem(viewerStorageKey, JSON.stringify({ mode: 'Pundit', language: 42, team: 'north-bank-fc' }));

    expect(loadViewer()).toEqual({ mode: 'Casual', language: 'fr' });

    localStorage.setItem(viewerStorageKey, '{not json');
    expect(loadViewer()).toEqual({ mode: 'Casual', language: 'fr' });
  });

  it('still works when storage is blocked', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('SecurityError');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('QuotaExceededError');
    });

    expect(() => saveViewer({ mode: 'Analyst', language: 'en' })).not.toThrow();
    expect(loadViewer().mode).toBe('Casual');
  });

  it('no longer carries a favourite club: a saved one is ignored and none goes in the query', () => {
    localStorage.setItem(viewerStorageKey, JSON.stringify({ mode: 'Casual', language: 'en', team: 'north-bank-fc' }));

    expect(loadViewer()).toEqual({ mode: 'Casual', language: 'en' });
    expect(viewerQuery({ mode: 'Analyst', language: 'fr' })).toEqual({ mode: 'Analyst', lang: 'fr' });
  });
});
