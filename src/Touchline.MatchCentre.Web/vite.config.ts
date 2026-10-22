/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The Match Centre (src/Touchline.MatchCentre) listens on this port under `dotnet run`.
const matchCentre = 'http://localhost:5054';

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
  },
});
