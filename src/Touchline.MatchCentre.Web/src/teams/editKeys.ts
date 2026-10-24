/**
 * Edit keys kept in the browser (ADR-053). Creating a fan team returns its edit key once (ADR-031); this keeps it in
 * localStorage under {@link editKeysStorageKey}, as `{ teamId: key }`, so the same browser can edit the team later.
 *
 * A key is a bearer secret: it's only ever sent in the `Touchline-Edit-Key` header. It never goes in a URL, a query
 * string, router state or a log.
 */

/** The localStorage key the edit keys are kept under. */
export const editKeysStorageKey = 'touchline.editKeys';

/** Team IDs are slugs (`north-bank-fc`, `fan-harbour-town-3f2a`); anything else in storage is ignored. */
const teamIdPattern = /^[A-Za-z0-9-]{1,100}$/;

/** The service issues 256-bit keys as base64url (43 characters); this accepts that alphabet with some slack. */
const editKeyPattern = /^[A-Za-z0-9_-]{16,128}$/;

/** True for a string shaped like a team ID. */
export function isTeamId(value: unknown): value is string {
  return typeof value === 'string' && teamIdPattern.test(value);
}

/** True for a string shaped like an edit key. */
export function isEditKey(value: unknown): value is string {
  return typeof value === 'string' && editKeyPattern.test(value);
}

/** A pasted key, tidied: surrounding whitespace and quotes removed. */
export function tidyEditKey(input: string): string {
  return input.trim().replace(/^["']|["']$/g, '');
}

/**
 * Every stored key, by team ID. Storage can be blocked, throw or hold anything (it's the visitor's own), so each
 * entry is checked and a bad one is dropped.
 */
export function loadEditKeys(): Record<string, string> {
  let saved: unknown;
  try {
    const raw = localStorage.getItem(editKeysStorageKey);
    saved = raw ? JSON.parse(raw) : undefined;
  } catch {
    return {};
  }
  if (typeof saved !== 'object' || saved === null || Array.isArray(saved)) return {};
  const keys: Record<string, string> = {};
  for (const [teamId, key] of Object.entries(saved)) {
    if (isTeamId(teamId) && isEditKey(key)) keys[teamId] = key;
  }
  return keys;
}

function storeEditKeys(keys: Record<string, string>): boolean {
  try {
    localStorage.setItem(editKeysStorageKey, JSON.stringify(keys));
    return true;
  } catch {
    return false;
  }
}

/** The key held for a team, or undefined. */
export function editKeyFor(teamId: string): string | undefined {
  const keys = loadEditKeys();
  return Object.hasOwn(keys, teamId) ? keys[teamId] : undefined;
}

/** Remembers a team's key. False when it isn't a valid key or storage refused it. */
export function rememberEditKey(teamId: string, key: string): boolean {
  if (!isTeamId(teamId) || !isEditKey(key)) return false;
  return storeEditKeys({ ...loadEditKeys(), [teamId]: key });
}

/** Forgets a team's key on this device, for example when the team is deleted or 404s. */
export function forgetEditKey(teamId: string): void {
  const keys = loadEditKeys();
  if (!Object.hasOwn(keys, teamId)) return;
  delete keys[teamId];
  storeEditKeys(keys);
}

/** The IDs of the teams this browser holds a key for. */
export function heldTeamIds(): ReadonlySet<string> {
  return new Set(Object.keys(loadEditKeys()));
}
