import { afterEach, describe, expect, it, vi } from 'vitest';
import { editKeyFor, editKeysStorageKey, forgetEditKey, heldTeamIds, isEditKey, loadEditKeys, rememberEditKey, tidyEditKey } from './editKeys';

const key = 'k7Qe3v9xZp2LmN8sT4wYb6Rj1Hc5Ud0Fa-_GqWiEoKs';

describe('editKeys', () => {
  afterEach(() => {
    localStorage.clear();
    vi.restoreAllMocks();
  });

  it('remembers, reads back and forgets a key by team ID', () => {
    expect(rememberEditKey('fan-harbour-town', key)).toBe(true);
    expect(editKeyFor('fan-harbour-town')).toBe(key);
    expect(JSON.parse(localStorage.getItem(editKeysStorageKey) ?? '{}')).toEqual({ 'fan-harbour-town': key });
    expect([...heldTeamIds()]).toEqual(['fan-harbour-town']);

    forgetEditKey('fan-harbour-town');

    expect(editKeyFor('fan-harbour-town')).toBeUndefined();
    expect(loadEditKeys()).toEqual({});
  });

  it('keeps other teams when one is added or forgotten', () => {
    rememberEditKey('fan-a', key);
    rememberEditKey('fan-b', key.replace('k', 'm'));
    forgetEditKey('fan-a');

    expect(Object.keys(loadEditKeys())).toEqual(['fan-b']);
  });

  it('ignores corrupt, hostile or mistyped storage', () => {
    localStorage.setItem(editKeysStorageKey, '{not json');
    expect(loadEditKeys()).toEqual({});

    localStorage.setItem(editKeysStorageKey, JSON.stringify(['fan-a', key]));
    expect(loadEditKeys()).toEqual({});

    localStorage.setItem(
      editKeysStorageKey,
      JSON.stringify({ 'fan-ok': key, 'bad id/../x': key, 'fan-short': 'abc', 'fan-num': 12, __proto__: key }),
    );
    expect(loadEditKeys()).toEqual({ 'fan-ok': key });
    expect(editKeyFor('__proto__')).toBeUndefined();
    expect(editKeyFor('constructor')).toBeUndefined();
  });

  it('refuses to store something that is not a key or not a team ID', () => {
    expect(rememberEditKey('fan-a', 'short')).toBe(false);
    expect(rememberEditKey('not a team', key)).toBe(false);
    expect(localStorage.getItem(editKeysStorageKey)).toBeNull();
  });

  it('survives storage that throws', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });

    expect(loadEditKeys()).toEqual({});
    expect(rememberEditKey('fan-a', key)).toBe(false);
    expect(() => forgetEditKey('fan-a')).not.toThrow();
  });

  it('tidies a pasted key and recognises the service shape', () => {
    expect(tidyEditKey(`  "${key}"\n`)).toBe(key);
    expect(isEditKey(key)).toBe(true);
    expect(isEditKey('has spaces in it but long enough')).toBe(false);
  });
});
