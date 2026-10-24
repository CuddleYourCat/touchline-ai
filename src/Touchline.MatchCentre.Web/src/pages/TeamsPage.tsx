import { useState } from 'react';
import { Link } from 'react-router';
import { heldTeamIds } from '../teams/editKeys';
import type { TeamSummary } from '../teams/teamDraft';
import { useTeams } from '../teams/useTeams';

function TeamCard({ team, canEdit }: { team: TeamSummary; canEdit: boolean }) {
  const fan = team.origin === 'Fan';
  return (
    <li className="team-card">
      <div className="team-card-top">
        <img src={team.crestUrl} alt="" width={44} height={44} className="team-crest" />
        <div className="team-card-names">
          <span className="team-card-name">{team.name}</span>
          <span className="tag">
            <span className="data">{team.shortName}</span> &middot; {team.venue}
          </span>
        </div>
        {fan && <span className="pill">fan-made</span>}
      </div>
      <div className="team-card-actions">
        <Link className="button" to={`/kickoff?home=${encodeURIComponent(team.id)}`} aria-label={`Play a match with ${team.name}`}>
          Play
        </Link>
        {canEdit && (
          <Link className="button button-ghost" to={`/teams/${encodeURIComponent(team.id)}/edit`} aria-label={`Edit ${team.name}`}>
            Edit
          </Link>
        )}
      </div>
    </li>
  );
}

function TeamSection({ title, teams, held, empty }: { title: string; teams: TeamSummary[]; held: ReadonlySet<string>; empty: string }) {
  const headingId = `teams-${title.toLowerCase().replace(/\W+/g, '-')}`;
  return (
    <section className="match-section" aria-labelledby={headingId}>
      <h2 id={headingId}>{title}</h2>
      {teams.length === 0 ? (
        <p className="muted">{empty}</p>
      ) : (
        <ul className="team-grid">
          {teams.map((t) => (
            <TeamCard key={t.id} team={t} canEdit={t.origin === 'Fan' && held.has(t.id)} />
          ))}
        </ul>
      )}
    </section>
  );
}

/**
 * `/teams`: the built-in clubs and the fan-made ones (ADR-031). "Your teams" are the fan teams this browser holds an
 * edit key for (ADR-053), and only they offer Edit.
 */
export function TeamsPage() {
  const { state, retry } = useTeams();
  const [held] = useState(heldTeamIds);

  return (
    <div className="teams-page">
      <section className="panel teams-intro">
        <div>
          <h1>Teams</h1>
          <p className="muted">
            Every club here is fictional and every match is synthetic. Fan-made clubs are built by visitors.
          </p>
        </div>
        <Link className="button" to="/teams/new">
          Build a team
        </Link>
      </section>

      {state.kind === 'loading' && (
        <p className="muted" aria-busy="true">
          Loading the clubs...
        </p>
      )}
      {state.kind === 'error' && (
        <section className="panel" role="alert">
          <p>We couldn't load the clubs just now.</p>
          <button type="button" className="button" onClick={retry}>
            Try again
          </button>
        </section>
      )}
      {state.kind === 'ready' && (
        <>
          {state.teams.some((t) => t.origin === 'Fan' && held.has(t.id)) && (
            <TeamSection
              title="Your teams"
              teams={state.teams.filter((t) => t.origin === 'Fan' && held.has(t.id))}
              held={held}
              empty=""
            />
          )}
          <TeamSection
            title="Built-in clubs"
            teams={state.teams.filter((t) => t.origin === 'BuiltIn')}
            held={held}
            empty="No built-in clubs are listed right now."
          />
          <TeamSection
            title="Fan-made clubs"
            teams={state.teams.filter((t) => t.origin === 'Fan')}
            held={held}
            empty="No fan-made clubs yet. Build the first one."
          />
        </>
      )}
    </div>
  );
}
