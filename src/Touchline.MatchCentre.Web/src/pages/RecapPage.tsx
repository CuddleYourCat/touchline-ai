import { Link, useParams } from 'react-router';
import { useMemo } from 'react';
import type { MatchDetail, MomentView } from '../live/matchStreamReducer';
import { HalfTimeCard } from '../match/HalfTimeCard';
import { scorerFullText, scorerLabel, scorersBySide, type Scorer } from '../match/playerFacts';
import { useGoalMoments, useMomentum, useRecap } from '../recap/useRecap';
import { useViewer } from '../viewer/viewer';
import { ViewerSettings } from '../viewer/ViewerSettings';

/** A side's scorers under the result: full names, minutes and (pen), grouped per player. */
function ScorerList({ name, scorers }: { name: string; scorers: Scorer[] }) {
  if (scorers.length === 0) return <div />;
  return (
    <ul className="recap-scorers-list" aria-label={`${name} scorers`}>
      {scorers.map((scorer) => (
        <li key={scorer.key} aria-label={scorerLabel(scorer)}>
          {scorerFullText(scorer)}
        </li>
      ))}
    </ul>
  );
}

function FinalScore({ detail, moments }: { detail: MatchDetail; moments: MomentView[] }) {
  const { home, away, score } = detail.summary;
  const scorers = useMemo(() => {
    const players = [...detail.homeLineup, ...detail.homeSubstitutes, ...detail.awayLineup, ...detail.awaySubstitutes];
    return scorersBySide(moments, (playerId) => players.find((p) => p.id === playerId)?.name);
  }, [detail, moments]);
  return (
    <>
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
      {(scorers.home.length > 0 || scorers.away.length > 0) && (
        <div className="recap-scorers">
          <ScorerList name={home.name} scorers={scorers.home} />
          <ScorerList name={away.name} scorers={scorers.away} />
        </div>
      )}
    </>
  );
}

/** `/matches/:id/recap`: the Producer's full-time recap, in the viewer's mode and language. */
export function RecapPage() {
  const { id = '' } = useParams();
  const [viewer, setViewer] = useViewer();
  const { state, retry } = useRecap(id, viewer);
  const moments = useGoalMoments(id);
  const momentum = useMomentum(id);
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
            <FinalScore detail={state.detail} moments={moments} />
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
      const paragraphs = recap.text.split(/\n{2,}/).filter((p) => p.trim().length > 0);
      return (
        <div className="recap">
          <div className="recap-tools">
            <ViewerSettings viewer={viewer} onChange={setViewer} />
          </div>
          <article className="panel recap-article" lang={recap.language}>
            <div className="match-card-top">
              <span className="pill">Full time</span>
              <span className="tag">{recap.source === 'Template' ? 'Quick recap' : `${recap.mode} recap`}</span>
            </div>
            <FinalScore detail={detail} moments={moments} />
            {/* A detail from an older server may carry no team stats; the card is left out rather than failing the page. */}
            {detail.state?.home && detail.state.away && (
              <HalfTimeCard detail={detail} state={detail.state} momentum={momentum} phase="full" />
            )}
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
