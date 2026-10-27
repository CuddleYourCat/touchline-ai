import { Link } from 'react-router';

/**
 * Any path the router doesn't know: a light-hearted "Offside!" page in the error page's layout, over its own artwork
 * (ADR-071 amendment 5).
 */
export function NotFoundPage() {
  return (
    <section className="panel error-page not-found-page" aria-labelledby="not-found-title">
      <h1 id="not-found-title">Offside!</h1>
      <p className="muted">That page isn&apos;t where it should be. The flag&apos;s up.</p>
      <div className="error-page-actions">
        <Link to="/" className="button">
          Matches
        </Link>
        <Link to="/teams" className="button button-ghost">
          Teams
        </Link>
      </div>
    </section>
  );
}
