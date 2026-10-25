import createClient, { type ClientOptions } from 'openapi-fetch';
import { maintenanceMiddleware } from './maintenance';
import type { paths } from './schema';

/**
 * Builds a typed Match Centre client. Paths, parameters and response bodies are checked against
 * `contracts/matchcentre.openapi.json` (ADR-027), through the types generated into schema.d.ts.
 * Every response passes the maintenance check (ADR-060).
 */
export function createApiClient(options: ClientOptions) {
  const client = createClient<paths>(options);
  client.use(maintenanceMiddleware);
  return client;
}

/** The app's client: same origin in production; the Vite dev server proxies `/api` in development. */
export const api = createApiClient({ baseUrl: '/' });
