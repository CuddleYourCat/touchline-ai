import { memo, useMemo } from 'react';
import { Link, useParams } from 'react-router';
import type { CommentaryLine, LiveMatch, MomentAnalysis, MomentView } from '../live/matchStreamReducer';
import { useMatchStream } from '../live/useMatchStream';
import { MomentumBars } from '../pitch/MomentumBars';
import { Pitch } from '../pitch/Pitch';
import { momentAnchor } from '../questions/askQuestion';
import { FanQuestions } from '../questions/FanQuestions';
import { useViewer } from '../viewer/viewer';
import { ViewerSettings } from '../viewer/ViewerSettings';

/** `PenaltyAwarded` becomes `Penalty awarded`. */
function kindLabel(kind: string): string {
  const words = kind.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

/**
 * A fan match between its 202 and its first event (ADR-050): the server sends it as Scheduled, with its line-ups and
 * no events, and the stream stays open until play starts. The first state message moves `lastSequence` on.
 */
function awaitingKickOff(match: LiveMatch): boolean {
  return match.detail.summary.status === 'Scheduled' && !match.fullTime && match.state.lastSequence === 0;
}

function Scoreboard({ match }: { match: LiveMatch }) {
  const { home, away } = match.detail.summary;
  const { score, clock } = match.state;
  const waiting = awaitingKickOff(match);
  const status = match.fullTime ? 'Full time' : waiting ? 'Kick-off in a moment' : `Live, ${clock.display}`;
  const label = `${status}, ${home.name} ${score.home}, ${away.name} ${score.away}`;

  return (
    <section className="panel scoreboard" aria-label={label}>
      <div className="match-card-top">
        {match.fullTime ? (
          <span className="pill">Full time</span>
        ) : waiting ? (
          <span className="pill" role="status">
            Kick-off in a moment…
          </span>
        ) : (
          <span className="pill pill-live">LIVE</span>
        )}
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
    <li className="moment" id={momentAnchor(moment.id)} tabIndex={-1}>
      <span className="moment-clock data">{moment.clock.display}</span>
      <div className="moment-body">
        <div className="moment-tags">
          <span className={moment.tier === 'Key' ? 'pill pill-key' : 'pill'}>
            {moment.kind === 'Substitution' && (
              <span className="moment-icon" aria-hidden="true">
                ⇄{' '}
              </span>
            )}
            {kindLabel(moment.kind)}
          </span>
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
  const [saved, setSaved] = useViewer();
  const { state, retry, retryMoments, viewer } = useMatchStream(id, saved);
  const moments = state.kind === 'ready' ? state.match.moments : undefined;
  const momentIds = useMemo(() => new Set(moments ? Object.keys(moments) : []), [moments]);

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
    case 'notStarted':
      return (
        <section className="panel">
          <h1>This match didn't start</h1>
          <p className="muted">
            Something went wrong before kick-off, so there's nothing to watch. <Link to="/kickoff">Pick the teams again</Link> or{' '}
            <Link to="/">go back to the matches</Link>.
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
          {state.match.fullTime && (
            <Link to={`/matches/${encodeURIComponent(id)}/recap`} className="panel recap-teaser">
              <span className="pill">Full time</span>
              <span>Read the full-time recap</span>
            </Link>
          )}
          <ViewerSettings
            viewer={saved}
            onChange={setSaved}
            clubs={[state.match.detail.summary.home, state.match.detail.summary.away].map(({ id: clubId, name }) => ({ id: clubId, name }))}
          />
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
              homeBench={state.match.detail.homeSubstitutes}
              awayBench={state.match.detail.awaySubstitutes}
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
          {/* Keyed by match: a new match mounts a new box, which aborts the old question and starts an empty list. */}
          <FanQuestions key={id} matchId={id} viewer={viewer} momentIds={momentIds} />
          <Timeline match={state.match} viewerMode={viewer.mode} />
        </div>
      );
  }
}
