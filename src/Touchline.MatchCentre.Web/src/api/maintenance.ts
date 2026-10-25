import type { Middleware } from 'openapi-fetch';

/**
 * Maintenance mode (ADR-060). While it's on, the server answers every page with a maintenance page
 * and every API call with a 503 carrying this header. A tab opened before maintenance began is still
 * running the app, so when it sees the header it reloads, and the reload lands on the server's page.
 * That page has no scripts, so the reload can't loop.
 */
export const maintenanceHeader = 'X-Touchline-Maintenance';

/** Seams for tests: jsdom can't reload, and the probe needs a stub `fetch`. */
export const maintenanceDeps = {
  reload: () => window.location.reload(),
  fetch: (input: string, init?: RequestInit) => globalThis.fetch(input, init),
};

let reloading = false;

/** Whether a response came from the maintenance short-circuit. */
export function isMaintenance(response: Response): boolean {
  return response.headers.get(maintenanceHeader) === 'on';
}

/** Reloads the page once if the response says maintenance is on. Returns whether it did. */
export function reloadIfMaintenance(response: Response): boolean {
  if (!isMaintenance(response)) return false;
  if (!reloading) {
    reloading = true;
    maintenanceDeps.reload();
  }
  return true;
}

/**
 * Asks the server whether maintenance is on, for callers that can't see response headers (the
 * `EventSource` behind the live stream). A `HEAD` request is answered by the short-circuit with no
 * body, and otherwise costs nothing: no endpoint maps `HEAD`. Network failures are ignored, so the
 * caller's own retry carries on.
 */
export async function checkForMaintenance(): Promise<boolean> {
  try {
    const response = await maintenanceDeps.fetch('/api/matches', { method: 'HEAD', cache: 'no-store' });
    return reloadIfMaintenance(response);
  } catch {
    return false;
  }
}

/**
 * The typed client's response hook: every API call passes through it, so a tab left open when
 * maintenance starts reloads at its next read. Writes are left to the caller, which shows the 503:
 * reloading on a save would throw away the form the fan just filled in. The response goes on to
 * the caller unchanged.
 */
export const maintenanceMiddleware: Middleware = {
  onResponse({ request, response }) {
    if (request.method === 'GET') reloadIfMaintenance(response);
    return undefined;
  },
};

/**
 * Pages with no polling or stream (the recap, the team builder, the kick-off sheet) make no call
 * until the fan acts, so a tab coming back into view asks once. Only on becoming visible, so an
 * idle background tab never wakes a Match Centre that has scaled to zero. Returns the unsubscribe.
 */
export function watchForMaintenanceOnFocus(): () => void {
  const onVisible = () => {
    if (document.visibilityState === 'visible') void checkForMaintenance();
  };
  document.addEventListener('visibilitychange', onVisible);
  return () => document.removeEventListener('visibilitychange', onVisible);
}

/** Test-only: forgets that a reload was started. */
export function resetMaintenanceForTests(): void {
  reloading = false;
}
