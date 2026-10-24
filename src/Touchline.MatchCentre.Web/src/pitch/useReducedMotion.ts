import { useSyncExternalStore } from 'react';

const query = '(prefers-reduced-motion: reduce)';

function mediaQuery(): MediaQueryList | null {
  return typeof window !== 'undefined' && typeof window.matchMedia === 'function' ? window.matchMedia(query) : null;
}

function subscribe(onChange: () => void): () => void {
  const mq = mediaQuery();
  mq?.addEventListener('change', onChange);
  return () => mq?.removeEventListener('change', onChange);
}

/** True when the visitor asked the OS for less motion. Browsers without `matchMedia` (jsdom) get false. */
export function useReducedMotion(): boolean {
  return useSyncExternalStore(subscribe, () => mediaQuery()?.matches ?? false, () => false);
}
