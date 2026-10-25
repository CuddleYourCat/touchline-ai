import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  checkForMaintenance,
  maintenanceDeps,
  maintenanceHeader,
  maintenanceMiddleware,
  resetMaintenanceForTests,
  watchForMaintenanceOnFocus,
} from './maintenance';
import { createApiClient } from './client';

function response(status: number, maintenance: boolean): Response {
  const headers = new Headers({ 'Content-Type': 'application/problem+json' });
  if (maintenance) headers.set(maintenanceHeader, 'on');
  return new Response('{}', { status, headers });
}

/** Runs the typed client's response hook as openapi-fetch would after a call to /api/matches. */
async function apiCallAnswered(answer: Response, method = 'GET') {
  return maintenanceMiddleware.onResponse?.({
    response: answer,
    request: new Request('http://touchline.test/api/matches', { method }),
    schemaPath: '/api/matches',
    params: {},
    id: 'test',
    options: {} as never,
  });
}

describe('maintenance mode', () => {
  let reload: ReturnType<typeof vi.fn<() => void>>;

  beforeEach(() => {
    resetMaintenanceForTests();
    reload = vi.fn<() => void>();
    vi.spyOn(maintenanceDeps, 'reload').mockImplementation(reload);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('reloads the page when an API call is answered by the maintenance page, and passes the response on', async () => {
    await expect(apiCallAnswered(response(503, true))).resolves.toBeUndefined();

    expect(reload).toHaveBeenCalledTimes(1);
  });

  it.each(['POST', 'PUT', 'DELETE'])(
    'leaves a %s to the caller, so a form the fan filled in is not thrown away',
    async (method) => {
      await expect(apiCallAnswered(response(503, true), method)).resolves.toBeUndefined();

      expect(reload).not.toHaveBeenCalled();
    },
  );

  it('registers the hook on the clients the app builds', async () => {
    const fetch = vi.fn().mockResolvedValue(response(503, true));
    const client = createApiClient({ baseUrl: 'http://touchline.test', fetch });

    await client.GET('/api/matches');

    expect(fetch).toHaveBeenCalledTimes(1);
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('asks once when the tab comes back into view, and not when it goes into the background', async () => {
    const probe = vi.spyOn(maintenanceDeps, 'fetch').mockResolvedValue(response(503, true));
    const visibility = vi.spyOn(document, 'visibilityState', 'get');
    const stop = watchForMaintenanceOnFocus();

    visibility.mockReturnValue('hidden');
    document.dispatchEvent(new Event('visibilitychange'));
    expect(probe).not.toHaveBeenCalled();

    visibility.mockReturnValue('visible');
    document.dispatchEvent(new Event('visibilitychange'));
    await vi.waitFor(() => expect(reload).toHaveBeenCalledTimes(1));

    stop();
    document.dispatchEvent(new Event('visibilitychange'));
    expect(probe).toHaveBeenCalledTimes(1);
  });

  it('leaves an ordinary failure to the caller', async () => {
    await apiCallAnswered(response(503, false));

    expect(reload).not.toHaveBeenCalled();
  });

  it('reloads only once, however many calls see maintenance', async () => {
    vi.spyOn(maintenanceDeps, 'fetch').mockResolvedValue(response(503, true));

    await Promise.all([
      apiCallAnswered(response(503, true)),
      apiCallAnswered(response(503, true)),
      checkForMaintenance(),
    ]);

    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('probes with a HEAD request for callers that cannot see headers', async () => {
    const probe = vi.spyOn(maintenanceDeps, 'fetch').mockResolvedValue(response(503, true));

    await expect(checkForMaintenance()).resolves.toBe(true);

    expect(probe).toHaveBeenCalledWith('/api/matches', { method: 'HEAD', cache: 'no-store' });
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('does nothing when the probe finds no maintenance', async () => {
    vi.spyOn(maintenanceDeps, 'fetch').mockResolvedValue(response(405, false));

    await expect(checkForMaintenance()).resolves.toBe(false);

    expect(reload).not.toHaveBeenCalled();
  });

  it('ignores a probe that cannot reach the server, so the stream keeps retrying', async () => {
    vi.spyOn(maintenanceDeps, 'fetch').mockRejectedValue(new TypeError('Failed to fetch'));

    await expect(checkForMaintenance()).resolves.toBe(false);

    expect(reload).not.toHaveBeenCalled();
  });
});
