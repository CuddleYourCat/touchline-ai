import { Link, useParams } from 'react-router';
import type { MatchDetail } from '../live/matchStreamReducer';
import { useRecap } from '../recap/useRecap';
import { useViewer } from '../viewer/viewer';
import { ViewerSettings } from '../viewer/ViewerSettings';

function FinalScore({ detail }: { detail: MatchDetail }) {
  const { home, away, score } = detail.summary;
  return (
    <div className="recap-score" aria-label={`Full time, ${home.name} ${score.home}, ${away.name} ${score.away}`}>
      <span className="scoreboard-team">
        <img src={home.crestUrl} alt="" width={40} height={40} />
        <span>{home.name}</span>
      </span>
      <span className="scoreboard-score data">
        {score.home} - {score.away}
      </span>
      <span className="scoreboard-team">
        <img src={away.crestUrl} alt="" width={40} height={40} />
        <span>{away.name}</span>
      </span>
    </div>
  );
}

/** `/matches/:id/recap`: the Producer's full-time recap, in the viewer's mode and language. */
export function RecapPage() {
  const { id = '' } = useParams();
  const [viewer, setViewer] = useViewer();
  const { state, retry } = useRecap(id, viewer);
  const back = (
    <Link to={`/matches/${encodeURIComponent(id)}`} className="back-link">
      Back to the match
    </Link>
  );

  switch (state.kind) {
    case 'loading':
      return (
        <section className="panel" aria-busy="true">
          <p className="muted">Loading the recap...</p>
        </section>
      );
    case 'notFound':
      return (
        <section className="panel">
          <h1>Match not found</h1>
          <p className="muted">
            There's no match with that ID. <Link to="/">Back to the matches</Link>.
          </p>
        </section>
      );
    case 'error':
      return (
        <section className="panel" role="alert">
          <p>We couldn't load this recap just now.</p>
          <button type="button" className="button" onClick={retry}>
            Try again
          </button>
        </section>
      );
    case 'notFullTime':
      return (
        <section className="panel">
          <h1>Recap</h1>
          <p className="muted">The recap is written after the final whistle. The match is still going.</p>
          {back}
        </section>
      );
    case 'unavailable':
      return (
        <div className="recap">
          <section className="panel">
            <FinalScore detail={state.detail} />
          </section>
          <section className="panel" role="alert">
            <p>The recap isn't available right now. The score above is final.</p>
            <button type="button" className="button" onClick={retry}>
              Try again
            </button>
          </section>
          {back}
        </div>
      );
    case 'ready': {
      const { detail, recap, provisional, waiting } = state;
      const clubs = [detail.summary.home, detail.summary.away].map(({ id: clubId, name }) => ({ id: clubId, name }));
      const paragraphs = recap.text.split(/\n{2,}/).filter((p) => p.trim().length > 0);
      return (
        <div className="recap">
          <ViewerSettings viewer={viewer} onChange={setViewer} clubs={clubs} />
          <article className="panel recap-article" lang={recap.language}>
            <div className="match-card-top">
              <span className="pill">Full time</span>
              <span className="tag">{recap.source === 'Template' ? 'Quick recap' : `${recap.mode} recap`}</span>
            </div>
            <FinalScore detail={detail} />
            <h1 className="recap-headline">{recap.headline}</h1>
            {paragraphs.map((p, i) => (
              <p key={i} className="recap-text">
                {p}
              </p>
            ))}
            <p className="tag">Synthetic match: every club and player is fictional.</p>
          </article>
          <div role="status" className="recap-status">
            {provisional && waiting && (
              <p className="banner banner-quiet">
                Full recap on its way<span className="dots" aria-hidden="true" />
              </p>
            )}
            {provisional && !waiting && (
              <div className="banner banner-quiet">
                <p>The full recap is taking longer than usual, so here's the quick version.</p>
                <button type="button" className="button" onClick={retry}>
                  Check again
                </button>
              </div>
            )}
          </div>
          {back}
        </div>
      );
    }
  }
}
