import { memo, useCallback, useId, useMemo, useState, type CSSProperties, type ReactNode } from 'react';
import { Link, useParams } from 'react-router';
import type { CommentaryLine, LiveMatch, MomentAnalysis, MomentView, OverlayCard } from '../live/matchStreamReducer';
import { useMatchReplay, type MatchReplay } from '../live/useMatchReplay';
import { useMatchStream, type Viewer } from '../live/useMatchStream';
import { GoalToast } from '../match/GoalToast';
import { HalfTimeCard } from '../match/HalfTimeCard';
import { LineupsDrawer } from '../match/LineupsDrawer';
import { MatchTimeline } from '../match/MatchTimeline';
import { footballMinute, playerFacts, scorerLabel, scorersBySide, scorerText, stripClockPrefix, type Scorer } from '../match/playerFacts';
import { clockLabel } from '../matches/clockLabel';
import { PlayerFlyout } from '../match/PlayerFlyout';
import { MomentumBars } from '../pitch/MomentumBars';
import { parseKit, tokenColours, type Kit } from '../pitch/kitColours';
import { Pitch } from '../pitch/Pitch';
import { momentAnchor } from '../questions/askQuestion';
import { FanQuestions } from '../questions/FanQuestions';
import { useTeamDetail } from '../teams/useTeams';
import { useViewer } from '../viewer/viewer';
import { ViewerSettings } from '../viewer/ViewerSettings';
import './MatchPage.css';

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

/**
 * The header clock: the running clock, or HT and FT while the match is paused or over (ADR-066). Added time reads as a
 * scoreboard does, `90+3'`, the same as the match cards on the home page.
 */
function clockText(match: LiveMatch): string {
  return match.fullTime ? 'FT' : match.state.halfTime ? 'HT' : clockLabel(match.state.clock);
}

/**
 * The colour key for one side (ADR-071): its kit colours in the shape its players have on the pitch, a circle for home
 * and a diamond for away, so the sides don't depend on colour alone. A club whose kit hasn't loaded keeps the house colours.
 */
function KitKey({ side, name, kit }: { side: 'home' | 'away'; name: string; kit: Kit | null }) {
  const colours = kit ? tokenColours(kit) : null;
  const style = colours ? ({ '--key-fill': colours.fill, '--key-rim': colours.rim } as CSSProperties) : undefined;
  return (
    <span
      className={`kit-key kit-key-${side}`}
      style={style}
      role="img"
      aria-label={`${name} colours, shown as ${side === 'home' ? 'circles' : 'diamonds'} on the pitch`}
    />
  );
}

/**
 * A side's scorers beside the score, like a TV score bug: `Ashworth 45+2', 90+3' (pen)`. The surname keeps it compact and
 * the CSS clips it to two lines; each entry carries the full name for a screen reader.
 */
function Scorers({ name, scorers }: { name: string; scorers: Scorer[] }) {
  if (scorers.length === 0) return null;
  return (
    <ul className="scoreboard-scorers" aria-label={`${name} scorers`}>
      {scorers.map((scorer) => (
        <li key={scorer.key} aria-label={scorerLabel(scorer)}>
          {scorerText(scorer)}
        </li>
      ))}
    </ul>
  );
}

interface ScoreboardProps {
  match: LiveMatch;
  replay: boolean;
  homeKit: Kit | null;
  awayKit: Kit | null;
  /** Each side's scorers so far, from the moments on show. */
  scorers: { home: Scorer[]; away: Scorer[] };
  /** The buttons at the end of the row: line-ups and settings. */
  children: ReactNode;
}

function Scoreboard({ match, replay, homeKit, awayKit, scorers, children }: ScoreboardProps) {
  const { home, away } = match.detail.summary;
  const { score, clock } = match.state;
  const waiting = awaitingKickOff(match);
  const status = replay
    ? `Replay, ${clockLabel(clock)}`
    : match.fullTime
      ? 'Full time'
      : match.state.halfTime
        ? 'Half time'
        : waiting
          ? 'Kick-off in a moment'
          : `Live, ${clockLabel(clock)}`;
  const hasScorers = scorers.home.length > 0 || scorers.away.length > 0;
  const label = `${status}, ${home.name} ${score.home}, ${away.name} ${score.away}`;

  return (
    <section className="panel scoreboard" aria-label={label}>
      <div className="scoreboard-teams">
        <span className="scoreboard-team scoreboard-team-home">
          <img src={home.crestUrl} alt={`${home.name} crest`} width={52} height={52} />
          <KitKey side="home" name={home.name} kit={homeKit} />
          <span className="scoreboard-name">
            <span>{home.name}</span>
          </span>
        </span>
        <div className="scoreboard-centre">
          <div className="scoreboard-status">
            {replay ? (
              <span className="pill">Replay</span>
            ) : match.fullTime ? (
              <span className="pill">Full time</span>
            ) : waiting ? (
              <span className="pill" role="status">
                Kick-off in a moment…
              </span>
            ) : (
              <span className="pill pill-live">LIVE</span>
            )}
            <span className="match-clock data">{clockText(match)}</span>
          </div>
          <div className={hasScorers ? 'scoreboard-scoreline scoreboard-scoreline-scorers' : 'scoreboard-scoreline'}>
            <div className="scoreboard-side scoreboard-side-home">
              <Scorers name={home.name} scorers={scorers.home} />
            </div>
            <span className="scoreboard-score data">
              {score.home} - {score.away}
            </span>
            <div className="scoreboard-side scoreboard-side-away">
              <Scorers name={away.name} scorers={scorers.away} />
            </div>
          </div>
        </div>
        <span className="scoreboard-team scoreboard-team-away">
          <img src={away.crestUrl} alt={`${away.name} crest`} width={52} height={52} />
          <KitKey side="away" name={away.name} kit={awayKit} />
          <span className="scoreboard-name">
            <span>{away.name}</span>
          </span>
        </span>
        <div className="scoreboard-tools">{children}</div>
      </div>
      <div className="match-card-meta scoreboard-meta">
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
  /** Starts a replay from this moment's sequence (ADR-067). */
  onReplay: (sequence: number) => void;
}

/** One moment. Memoised, so a message about another moment doesn't re-render it. */
const MomentItem = memo(function MomentItem({ moment, line, analysis, viewerMode, onReplay }: MomentItemProps) {
  const otherMode = line !== undefined && line.mode !== viewerMode;
  const minute = footballMinute(moment.clock);
  return (
    <li className="moment" id={momentAnchor(moment.id)} tabIndex={-1}>
      <span className="moment-clock data">{minute}</span>
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
          {otherMode && <span className="tag">{line.mode === 'Analyst' ? 'Analyst view' : 'Casual view'}</span>}
        </div>
        <p className="moment-text">{stripClockPrefix(line ? line.text : moment.reason)}</p>
        {analysis && <p className="moment-analysis muted">{analysis.text}</p>}
      </div>
      <button
        type="button"
        className={moment.tier === 'Key' ? 'moment-replay moment-replay-key' : 'moment-replay'}
        aria-label={`Replay from ${minute}, ${kindLabel(moment.kind)}`}
        onClick={() => onReplay(moment.sequence)}
      >
        <span aria-hidden="true">▶</span> Replay
      </button>
    </li>
  );
});

interface MomentsListProps {
  match: LiveMatch;
  viewerMode: CommentaryLine['mode'];
  onReplay: (sequence: number) => void;
}

function MomentsList({ match, viewerMode, onReplay }: MomentsListProps) {
  const moments = useMemo(() => Object.values(match.moments).sort((a, b) => b.sequence - a.sequence), [match.moments]);
  return (
    <section className="match-section match-moments" aria-label="Moments">
      <h2>Moments</h2>
      {moments.length === 0 ? (
        <p className="muted">No moments yet.</p>
      ) : (
        <ol className="moment-list">
          {moments.map((moment) => (
            <MomentItem
              key={moment.id}
              moment={moment}
              line={match.commentary[moment.id]}
              analysis={match.analysis[moment.id]}
              viewerMode={viewerMode}
              onReplay={onReplay}
            />
          ))}
        </ol>
      )}
    </section>
  );
}

interface GoalLayerProps {
  card: OverlayCard | null;
  scorerName: string | null;
  teamName: string;
  homeName: string;
  awayName: string;
  home: number;
  away: number;
  minute: string;
  replay: boolean;
  onDone: () => void;
}

/** The goal toast, memoised on plain values so a commentary line elsewhere on the page doesn't re-render it. */
const GoalLayer = memo(function GoalLayer({
  card,
  scorerName,
  teamName,
  homeName,
  awayName,
  home,
  away,
  minute,
  replay,
  onDone,
}: GoalLayerProps) {
  return (
    <GoalToast
      card={card}
      scorerName={scorerName}
      teamName={teamName}
      score={{ home, away, homeName, awayName }}
      minute={minute}
      replay={replay}
      onDone={onDone}
    />
  );
});

/** "Replay · 12 min behind live", "Replay finished" at full time, or just "Replay" on a finished match or before the playhead has a time. */
function replayLabel(replay: MatchReplay, liveFinished: boolean): string {
  if (replay.status === 'loading') return 'Replay · loading…';
  if (replay.status === 'ended') return 'Replay finished';
  const behind = replay.behindLiveSeconds;
  if (liveFinished || behind === null) return 'Replay';
  const minutes = Math.round(behind / 60);
  return minutes < 1 ? 'Replay · under a minute behind live' : `Replay · ${minutes} min behind live`;
}

function ReplayBar({ replay, liveFinished }: { replay: MatchReplay; liveFinished: boolean }) {
  // The announcer is always mounted, so a screen reader is already listening when a replay starts or ends.
  const on = replay.status !== 'idle';
  const phase = replay.status === 'ended' ? 'ended' : on ? 'on' : 'idle';
  const [announced, setAnnounced] = useState({ phase: 'idle', text: '' });
  if (announced.phase !== phase) {
    setAnnounced({ phase, text: phase === 'ended' ? 'Replay finished' : phase === 'on' ? 'Replay started' : 'Back to live' });
  }

  return (
    <>
      <div className="visually-hidden" role="status" aria-label="Replay announcements">
        {announced.text}
      </div>
      {replay.status === 'error' ? (
        <div className="replay-bar replay-bar-error" role="alert">
          <span>We couldn't load that replay.</span>
          <button type="button" className="button" onClick={replay.backToLive}>
            Back to live
          </button>
        </div>
      ) : on ? (
        <div className="replay-bar" data-testid="replay-badge">
          <span className="replay-badge data">{replayLabel(replay, liveFinished)}</span>
          <button type="button" className="button" onClick={replay.backToLive}>
            Back to live
          </button>
        </div>
      ) : null}
    </>
  );
}

interface ReadyMatchProps {
  id: string;
  live: LiveMatch;
  stale: boolean;
  momentsFailed: boolean;
  retryMoments: () => void;
  viewer: Viewer;
  saved: Viewer;
  onViewerChange: (next: Viewer) => void;
  momentIds: ReadonlySet<string>;
}

/** A club's kit from its team record, or null until that loads (or if it can't), when the pitch keeps the house colours. */
function useKit(teamId: string) {
  const { state } = useTeamDetail(teamId);
  const colours = state.kind === 'ready' ? state.detail.team?.colours : undefined;
  return useMemo(() => parseKit(colours), [colours]);
}

/** The match once it has loaded: the live view, or a replay of part of it in its place (ADR-067). */
function ReadyMatch({ id, live, stale, momentsFailed, retryMoments, viewer, saved, onViewerChange, momentIds }: ReadyMatchProps) {
  const replay = useMatchReplay(id, live, { viewer });
  const replaying = replay.status !== 'idle' && replay.match !== null;
  const shown = replaying && replay.match ? replay.match : live;
  const { detail } = shown;
  const homeName = detail.summary.home.name;
  const awayName = detail.summary.away.name;
  const { start } = replay;
  const homeKit = useKit(detail.summary.home.id);
  const awayKit = useKit(detail.summary.away.id);

  const momentList = useMemo(() => Object.values(shown.moments), [shown.moments]);
  // The timeline keeps every moment during a replay, so each icon stays clickable.
  const liveMomentList = useMemo(() => Object.values(live.moments), [live.moments]);
  const facts = useMemo(() => playerFacts(momentList), [momentList]);

  const replayFromSequence = useCallback((sequence: number) => start({ sequence }), [start]);
  // The live match has every moment; the replay's view has only those already played.
  const replayFromMoment = useCallback(
    (momentId: string) => {
      const found = live.moments[momentId];
      if (found) start({ sequence: found.sequence });
    },
    [live.moments, start],
  );

  // The goal card on show: the newest goal card of the view on screen, once it has arrived. Switching between live and
  // a replay shows nothing new, and a restarted replay clears the old card.
  const latestGoal = shown.overlays.findLast((card) => card.type === 'goal') ?? null;
  const [goal, setGoal] = useState<{ replaying: boolean; seen: OverlayCard | null; card: OverlayCard | null }>({
    replaying,
    seen: latestGoal,
    card: null,
  });
  if (goal.replaying !== replaying) setGoal({ replaying, seen: latestGoal, card: null });
  else if (goal.seen !== latestGoal) setGoal({ replaying, seen: latestGoal, card: latestGoal });
  const goalDone = useCallback(() => setGoal((g) => ({ ...g, card: null })), []);

  const allPlayers = useMemo(
    () => [
      ...detail.homeLineup.map((p) => ({ p, team: homeName })),
      ...detail.homeSubstitutes.map((p) => ({ p, team: homeName })),
      ...detail.awayLineup.map((p) => ({ p, team: awayName })),
      ...detail.awaySubstitutes.map((p) => ({ p, team: awayName })),
    ],
    [detail, homeName, awayName],
  );
  const scorerId = goal.card?.payload.playerId;
  const scorer = scorerId ? allPlayers.find(({ p }) => p.id === scorerId) : undefined;
  const goalTeam = goal.card?.payload.team === 'Away' ? awayName : homeName;

  const scorers = useMemo(
    () => scorersBySide(momentList, (playerId) => allPlayers.find(({ p }) => p.id === playerId)?.p.name),
    [momentList, allPlayers],
  );

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const selected = selectedId ? allPlayers.find(({ p }) => p.id === selectedId) : undefined;
  const [lineupsOpen, setLineupsOpen] = useState(false);
  const lineupsId = `${useId()}-lineups`;

  return (
    <div className="match-live match-page">
      {stale && !live.fullTime && (
        <p className="banner" role="status">
          Reconnecting…
        </p>
      )}
      <Scoreboard match={shown} replay={replay.status !== 'idle'} homeKit={homeKit} awayKit={awayKit} scorers={scorers}>
        <button
          type="button"
          className="button button-quiet"
          aria-expanded={lineupsOpen}
          aria-controls={lineupsId}
          onClick={() => setLineupsOpen((o) => !o)}
        >
          Line-ups
        </button>
        <ViewerSettings showPitchOption viewer={saved} onChange={onViewerChange} />
      </Scoreboard>
      <ReplayBar replay={replay} liveFinished={live.fullTime} />
      {live.fullTime && (
        <Link to={`/matches/${encodeURIComponent(id)}/recap`} className="panel recap-teaser">
          <span className="pill">Full time</span>
          <span>Read the full-time recap</span>
        </Link>
      )}
      {momentsFailed && (
        <div className="banner">
          <p>Earlier moments couldn't be loaded.</p>
          <button type="button" className="button" onClick={retryMoments}>
            Try again
          </button>
        </div>
      )}
      <div className={shown.momentum.length > 0 ? 'match-layout' : 'match-layout match-layout-no-momentum'}>
        <div className="match-stage">
          <Pitch
            shape={shown.shape}
            ballPath={shown.ballPath}
            events={shown.events}
            homeLineup={detail.homeLineup}
            awayLineup={detail.awayLineup}
            homeBench={detail.homeSubstitutes}
            awayBench={detail.awaySubstitutes}
            overlays={shown.overlays}
            replay={replaying}
            homeName={homeName}
            awayName={awayName}
            homeKit={homeKit}
            awayKit={awayKit}
          />
          <GoalLayer
            card={goal.card}
            scorerName={scorer?.p.name ?? null}
            teamName={goalTeam}
            homeName={homeName}
            awayName={awayName}
            home={shown.state.score.home}
            away={shown.state.score.away}
            minute={goal.card ? footballMinute(goal.card.at) : ''}
            replay={replaying}
            onDone={goalDone}
          />
          {shown.state.halfTime && !shown.fullTime && (
            <HalfTimeCard detail={detail} state={shown.state} momentum={shown.momentum} replay={replaying} />
          )}
        </div>
        <div className="match-timeline">
          <MatchTimeline
            detail={detail}
            moments={liveMomentList}
            clock={live.state.clock}
            fullTime={live.fullTime}
            replayClock={replay.clock}
            onSelectMoment={replayFromMoment}
            onSeek={start}
          />
        </div>
        {shown.momentum.length > 0 && (
          <div className="match-momentum">
            <MomentumBars readings={shown.momentum} homeName={homeName} awayName={awayName} />
          </div>
        )}
        <MomentsList match={shown} viewerMode={viewer.mode} onReplay={replayFromSequence} />
      </div>
      {lineupsOpen && (
        <LineupsDrawer
          id={lineupsId}
          detail={detail}
          moments={momentList}
          selectedPlayerId={selectedId}
          onSelectPlayer={setSelectedId}
          onClose={() => setLineupsOpen(false)}
          suspended={selected !== undefined}
        />
      )}
      {/* Keyed by match: a new match mounts a new chat, which aborts the old question and starts an empty list. */}
      <FanQuestions key={id} matchId={id} viewer={viewer} momentIds={momentIds} />
      {selected && (
        <PlayerFlyout
          player={selected.p}
          withDrawer={lineupsOpen}
          teamName={selected.team}
          facts={facts[selected.p.id]}
          season={detail.summary.season ?? null}
          onClose={() => setSelectedId(null)}
          onSelectMoment={(momentId) => {
            setSelectedId(null);
            setLineupsOpen(false);
            replayFromMoment(momentId);
          }}
        />
      )}
    </div>
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
        <ReadyMatch
          key={id}
          id={id}
          live={state.match}
          stale={state.stale}
          momentsFailed={state.momentsFailed}
          retryMoments={retryMoments}
          viewer={viewer}
          saved={saved}
          onViewerChange={setSaved}
          momentIds={momentIds}
        />
      );
  }
}
