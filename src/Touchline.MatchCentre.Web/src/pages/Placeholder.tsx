import type { ReactNode } from 'react';

/** A page that's routed but not built yet. */
export function Placeholder({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <section className="panel">
      <h1>{title}</h1>
      <p className="muted">{children ?? 'Coming in a later slice.'}</p>
    </section>
  );
}
