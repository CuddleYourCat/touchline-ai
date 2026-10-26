/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Under the Aspire AppHost, WithReference(matchcentre) injects the Match Centre's endpoint, and
// the dev server's port arrives as --port. A plain `npm run dev` falls back to the port the
// Match Centre (src/Touchline.MatchCentre) listens on under `dotnet run`.
const matchCentre =
  process.env.MATCHCENTRE_HTTP ??
  process.env.services__matchcentre__http__0 ??
  'http://localhost:5054';

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': { target: matchCentre, changeOrigin: true },
      '/openapi': { target: matchCentre, changeOrigin: true },
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test-setup.ts'],
    restoreMocks: true,
    unstubGlobals: true,
    // Slow user-event form tests can pass the 5 s default when the files run in parallel.
    testTimeout: 15_000,
  },
});
