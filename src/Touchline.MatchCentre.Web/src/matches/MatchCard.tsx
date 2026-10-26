import { Link } from 'react-router';
import { clockLabel } from './clockLabel';
import type { MatchSummary } from './useMatches';

const kindLabels: Record<MatchSummary['kind'], string> = {
  Exhibition: 'Exhibition',
  Fixture: 'Fixture',
  Replay: 'Replay',
};

function formatKickOff(kickOffUtc: string): string {
  const date = new Date(kickOffUtc);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleString(undefined, {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function StatusPill({ status }: { status: MatchSummary['status'] }) {
  switch (status) {
    case 'Live':
      return <span className="pill pill-live">LIVE</span>;
    case 'FullTime':
      return <span className="pill">Full time</span>;
    default:
      return <span className="pill">Scheduled</span>;
  }
}

/** The card's accessible name: status, clock and score for a started match, the fixture and kick-off otherwise. */
function accessibleName(match: MatchSummary): string {
  const { home, away, score, clock } = match;
  const result = `${home.name} ${score.home}, ${away.name} ${score.away}`;
  switch (match.status) {
    case 'Live':
      return `Live, ${clockLabel(clock)}, ${result}`;
    case 'FullTime':
      return `Full time, ${result}`;
    case 'Scheduled': {
      const kickOff = formatKickOff(match.kickOffUtc);
      return `Scheduled, ${home.name} versus ${away.name}${kickOff ? `, kick-off ${kickOff}` : ''}`;
    }
    default:
      return `${String(match.status)}, ${result}`;
  }
}

/** One match in the list: crests, short names, score, clock, status and details, linking to the match. */
export function MatchCard({ match }: { match: MatchSummary }) {
  const { home, away, score, clock } = match;
  const showScore = match.status !== 'Scheduled';
  const label = accessibleName(match);

  return (
    <Link to={`/matches/${encodeURIComponent(match.id)}`} className="match-card" aria-label={label}>
      <div className="match-card-top">
        <StatusPill status={match.status} />
        {showScore && <span className="match-clock match-card-clock data">{clockLabel(clock)}</span>}
      </div>
      <div className="match-card-teams">
        <span className="match-team">
          <img src={home.crestUrl} alt={`${home.name} crest`} width={56} height={56} />
          <span className="match-team-name">{home.shortName}</span>
        </span>
        <span className="match-score data">{showScore ? `${score.home} - ${score.away}` : 'v'}</span>
        <span className="match-team">
          <img src={away.crestUrl} alt={`${away.name} crest`} width={56} height={56} />
          <span className="match-team-name">{away.shortName}</span>
        </span>
      </div>
      <div className="match-card-foot">
        <span className="match-card-details muted">
          <span>{kindLabels[match.kind]}</span>
          {match.round != null && <span>Round {match.round}</span>}
          <time dateTime={match.kickOffUtc}>{formatKickOff(match.kickOffUtc)}</time>
          <span className="match-source data">{match.dataSource}</span>
        </span>
        <span className="match-card-cue" aria-hidden="true">
          {match.status === 'Scheduled' ? 'Details' : 'Watch'} &rarr;
        </span>
      </div>
    </Link>
  );
}
