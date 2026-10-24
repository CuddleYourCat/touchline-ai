import { memo, useMemo } from 'react';
import { Link, useParams } from 'react-router';
import type { CommentaryLine, LiveMatch, MomentAnalysis, MomentView } from '../live/matchStreamReducer';
import { useMatchStream } from '../live/useMatchStream';
import { MomentumBars } from '../pitch/MomentumBars';
import { Pitch } from '../pitch/Pitch';

/** `PenaltyAwarded` becomes `Penalty awarded`. */
function kindLabel(kind: string): string {
  const words = kind.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

function Scoreboard({ match }: { match: LiveMatch }) {
  const { home, away } = match.detail.summary;
  const { score, clock } = match.state;
  const label = `${match.fullTime ? 'Full time' : `Live, ${clock.display}`}, ${home.name} ${score.home}, ${away.name} ${score.away}`;

  return (
    <section className="panel scoreboard" aria-label={label}>
      <div className="match-card-top">
        {match.fullTime ? <span className="pill">Full time</span> : <span className="pill pill-live">LIVE</span>}
        <span className="match-clock data">{clock.display}</span>
      </div>
      <div className="scoreboard-teams">
        <span className="scoreboard-team">
          <img src={home.crestUrl} alt={`${home.name} crest`} width={56} height={56} />
          <span>{home.name}</span>
        </span>
        <span className="scoreboard-score data">
          {score.home} - {score.away}
        </span>
        <span className="scoreboard-team">
          <img src={away.crestUrl} alt={`${away.name} crest`} width={56} height={56} />
          <span>{away.name}</span>
        </span>
      </div>
      <div className="match-card-meta muted">
        <span>{match.detail.venue}</span>
        <span>Referee: {match.detail.referee}</span>
      </div>
    </section>
  );
}

interface MomentItemProps {
  moment: MomentView;
  line: CommentaryLine | undefined;
  analysis: MomentAnalysis | undefined;
  /** The viewer's mode as a line names it, so a line written for the other mode gets a tag. */
  viewerMode: CommentaryLine['mode'];
}

/** One moment. Memoised, so a message about another moment doesn't re-render it. */
const MomentItem = memo(function MomentItem({ moment, line, analysis, viewerMode }: MomentItemProps) {
  const otherMode = line !== undefined && line.mode !== viewerMode;
  return (
    <li className="moment">
      <span className="moment-clock data">{moment.clock.display}</span>
      <div className="moment-body">
        <div className="moment-tags">
          <span className={moment.tier === 'Key' ? 'pill pill-key' : 'pill'}>{kindLabel(moment.kind)}</span>
          {line?.source === 'Template' && <span className="tag">Template</span>}
          {otherMode && <span className="tag">{line.mode === 'Analyst' ? 'Analyst view' : 'Casual view'}</span>}
        </div>
        <p className="moment-text">{line ? line.text : moment.reason}</p>
        {analysis && <p className="moment-analysis muted">{analysis.text}</p>}
      </div>
    </li>
  );
});

function Timeline({ match, viewerMode }: { match: LiveMatch; viewerMode: CommentaryLine['mode'] }) {
  const moments = useMemo(() => Object.values(match.moments).sort((a, b) => b.sequence - a.sequence), [match.moments]);
  return (
    <section className="match-section" aria-label="Moments">
      <h2>Moments</h2>
      {moments.length === 0 ? (
        <p className="muted">No moments yet.</p>
      ) : (
        <ol className="timeline">
          {moments.map((moment) => (
            <MomentItem
              key={moment.id}
              moment={moment}
              line={match.commentary[moment.id]}
              analysis={match.analysis[moment.id]}
              viewerMode={viewerMode}
            />
          ))}
        </ol>
      )}
    </section>
  );
}

/** `/matches/:id`: one match, live or finished. */
export function MatchPage() {
  const { id = '' } = useParams();
  const { state, retry, retryMoments, viewer } = useMatchStream(id);

  switch (state.kind) {
    case 'loading':
      return (
        <section className="panel" aria-busy="true">
          <p className="muted">Loading the match...</p>
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
          <p>We couldn't load this match just now.</p>
          <button type="button" className="button" onClick={retry}>
            Try again
          </button>
        </section>
      );
    case 'ready':
      return (
        <div className="match-live">
          {state.stale && !state.match.fullTime && (
            <p className="banner" role="status">
              Reconnecting…
            </p>
          )}
          <Scoreboard match={state.match} />
          {state.momentsFailed && (
            <div className="banner">
              <p>Earlier moments couldn't be loaded.</p>
              <button type="button" className="button" onClick={retryMoments}>
                Try again
              </button>
            </div>
          )}
          <div className="match-visuals">
            <Pitch
              shape={state.match.shape}
              ballPath={state.match.ballPath}
              events={state.match.events}
              homeLineup={state.match.detail.homeLineup}
              awayLineup={state.match.detail.awayLineup}
              overlays={state.match.overlays}
              homeName={state.match.detail.summary.home.name}
              awayName={state.match.detail.summary.away.name}
            />
            <MomentumBars
              readings={state.match.momentum}
              homeName={state.match.detail.summary.home.name}
              awayName={state.match.detail.summary.away.name}
            />
          </div>
          <Timeline match={state.match} viewerMode={viewer.mode} />
        </div>
      );
  }
}
