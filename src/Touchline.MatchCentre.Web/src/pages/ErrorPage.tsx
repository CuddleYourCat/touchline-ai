import { Link, useRouteError } from 'react-router';

function messageOf(error: unknown): string {
  if (error instanceof Error) return error.message;
  if (typeof error === 'object' && error !== null && 'statusText' in error)
    return String((error as { statusText: unknown }).statusText);
  return String(error);
}

/**
 * What a page shows when it throws while rendering (ADR-071 amendment 4), in place of React Router's developer screen.
 * The error text is for developers: it appears, collapsed, only in a dev build.
 */
export function ErrorPage() {
  const error = useRouteError();
  return (
    <section className="panel error-page" role="alert">
      <h1>Something went wrong</h1>
      <p className="muted">
        This page ran into a problem. Your matches and teams are safe. Try again, or head back to the matches.
      </p>
      <div className="error-page-actions">
        <button type="button" className="button" onClick={() => window.location.reload()}>
          Try again
        </button>
        <Link to="/" className="button button-ghost" reloadDocument>
          Matches
        </Link>
      </div>
      {import.meta.env.DEV && (
        <details className="error-page-details">
          <summary>Error details</summary>
          <pre>{messageOf(error)}</pre>
        </details>
      )}
    </section>
  );
}
