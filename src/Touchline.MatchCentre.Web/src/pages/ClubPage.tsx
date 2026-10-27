import { useState } from 'react';
import { Link, useParams } from 'react-router';
import { parseKit } from '../pitch/kitColours';
import { heldTeamIds } from '../teams/editKeys';
import type { PlayerPosition, TeamDetail } from '../teams/teamDraft';
import { useTeamDetail } from '../teams/useTeams';
import './ClubPage.css';

const groups: { position: PlayerPosition; heading: string; short: string }[] = [
  { position: 'Goalkeeper', heading: 'Goalkeepers', short: 'GK' },
  { position: 'Defender', heading: 'Defenders', short: 'DF' },
  { position: 'Midfielder', heading: 'Midfielders', short: 'MF' },
  { position: 'Forward', heading: 'Forwards', short: 'FW' },
];

/** "HighPress" as "High press". */
function styleLabel(style: string): string {
  const spaced = style.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

function BackLink() {
  return (
    <Link className="club-back" to="/teams">
      <span aria-hidden="true">&larr;</span> Back to Teams
    </Link>
  );
}

function Squad({ detail }: { detail: TeamDetail }) {
  return (
    <section className="club-panel" aria-labelledby="club-squad">
      <h2 id="club-squad" className="club-panel-title">
        Squad
      </h2>
      {groups.map(({ position, heading, short }) => {
        const players = detail.squad.filter((p) => p.position === position).sort((a, b) => a.shirtNumber - b.shirtNumber);
        if (players.length === 0) return null;
        return (
          <section key={position} aria-label={heading}>
            <h3 className="club-subheading">{heading}</h3>
            <ul className="club-list">
              {players.map((p) => (
                <li key={p.id} className="club-row">
                  <span className="club-number">{p.shirtNumber}</span>
                  <span className="club-player">{p.name}</span>
                  <span className="club-position" title={position}>
                    {short}
                  </span>
                </li>
              ))}
            </ul>
          </section>
        );
      })}
    </section>
  );
}

function Club({ detail, canEdit }: { detail: TeamDetail; canEdit: boolean }) {
  const { team, manager } = detail;
  const kit = parseKit(team.colours);
  const fan = team.origin === 'Fan';
  return (
    <div className="club-page">
      <BackLink />
      <header className="club-panel club-header">
        <img src={team.crestUrl} alt={`${team.name} crest`} width={96} height={96} className="team-crest" />
        <div className="club-header-text">
          <h1 className="club-name">{team.name}</h1>
          <p className="muted">
            <span className="data">{team.shortName}</span> &middot; {team.venue}
          </p>
          <p>
            <span className="pill">{fan ? 'fan-made' : 'built-in'}</span>
          </p>
        </div>
        <div className="club-actions">
          <Link className="button" to={`/kickoff?home=${encodeURIComponent(team.id)}`} aria-label={`Play a match with ${team.name}`}>
            Play
          </Link>
          {canEdit && (
            <Link className="button button-ghost" to={`/teams/${encodeURIComponent(team.id)}/edit`} aria-label={`Edit ${team.name}`}>
              Edit
            </Link>
          )}
        </div>
      </header>

      <div className="club-columns">
        <Squad detail={detail} />
        <section className="club-panel" aria-labelledby="club-details">
          <h2 id="club-details" className="club-panel-title">
            Club details
          </h2>
          <dl className="club-facts">
            <dt>Manager</dt>
            <dd>{manager ? manager.name : 'Not listed'}</dd>
            {manager && (
              <>
                <dt>Style</dt>
                <dd>{styleLabel(manager.style)}</dd>
              </>
            )}
            <dt>Stadium</dt>
            <dd>{team.venue}</dd>
            <dt>Short name</dt>
            <dd className="data">{team.shortName}</dd>
            <dt>Kit colours</dt>
            <dd>
              {kit ? (
                <span className="club-swatches">
                  <span className="club-swatch" style={{ background: kit.primary }} role="img" aria-label={`Home colour ${kit.primary}`} />
                  <span className="club-swatch" style={{ background: kit.secondary }} role="img" aria-label={`Trim colour ${kit.secondary}`} />
                  <span className="data">
                    {kit.primary} / {kit.secondary}
                  </span>
                </span>
              ) : (
                'Not listed'
              )}
            </dd>
            <dt>Origin</dt>
            <dd>{fan ? 'Fan-made' : 'Built-in'}</dd>
          </dl>
        </section>
      </div>
    </div>
  );
}

/** `/teams/:id`: one club, with its squad by position, manager, stadium and kit. */
export function ClubPage() {
  const { id } = useParams();
  const { state, retry } = useTeamDetail(id);
  const [held] = useState(heldTeamIds);

  switch (state.kind) {
    case 'idle':
    case 'loading':
      return (
        <section className="panel" aria-busy="true">
          <BackLink />
          <p className="muted">Loading the club...</p>
        </section>
      );
    case 'notFound':
      return (
        <section className="panel">
          <BackLink />
          <h1>Club not found</h1>
          <p className="muted">There's no club with that ID. It may have been deleted.</p>
        </section>
      );
    case 'error':
      return (
        <section className="panel" role="alert">
          <BackLink />
          <p>We couldn't load this club just now.</p>
          <button type="button" className="button" onClick={retry}>
            Try again
          </button>
        </section>
      );
    case 'ready':
      return <Club detail={state.detail} canEdit={state.detail.team.origin === 'Fan' && held.has(state.detail.team.id)} />;
  }
}
