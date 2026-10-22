import createClient from 'openapi-fetch';
import type { paths } from './schema';

/**
 * The typed Match Centre client. Paths, parameters and response bodies are checked against
 * `contracts/matchcentre.openapi.json` (ADR-027), through the types generated into schema.d.ts.
 * Same origin in production; the Vite dev server proxies `/api` in development.
 */
export const api = createClient<paths>({ baseUrl: '/' });
