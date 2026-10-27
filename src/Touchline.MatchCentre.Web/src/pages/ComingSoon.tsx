import { Link } from 'react-router';
// The one import of the Coming Soon image, which the maintenance page also serves (ADR-072): swap the file to change both.
import comingSoon from '../assets/coming-soon.webp';

/** The holding page shown instead of the kick-off form while fan matches can't be started (ADR-072). */
export function ComingSoon() {
  return (
    <section className="coming-soon" style={{ backgroundImage: `url(${comingSoon})` }} aria-labelledby="coming-soon-title">
      <div className="coming-soon-body">
        <p className="coming-soon-label" id="coming-soon-title">
          Coming Soon
        </p>
        <p className="coming-soon-copy">
          Fan matches kick off soon. In the meantime, watch the live and recent matches, or build your own team ready for the first
          whistle.
        </p>
        <div className="coming-soon-links">
          <Link to="/" className="button">
            Matches
          </Link>
          <Link to="/teams/new" className="button button-ghost">
            Build a team
          </Link>
        </div>
      </div>
    </section>
  );
}
