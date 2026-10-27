import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router';
import { api } from '../api/client';
import { PlayerFlyout } from '../match/PlayerFlyout';
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

type Player = TeamDetail['squad'][number];

/**
 * The season of the most recent league match, for the player card's season stats. There's no "current season" endpoint,
 * so it comes from the matches list: the latest match with a season. Null until it loads, or if no match has one (fan
 * clubs play exhibitions only), and a failed request is just null.
 */
function useLatestSeason(): string | null {
  const [season, setSeason] = useState<string | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    api
      .GET('/api/matches', { signal: controller.signal })
      .then(({ data }) => {
        if (controller.signal.aborted || !Array.isArray(data)) return;
        const latest = data
          .filter((m) => m.season !== null && m.season !== undefined)
          .sort((a, b) => b.kickOffUtc.localeCompare(a.kickOffUtc))[0];
        setSeason(latest?.season ?? null);
      })
      .catch(() => undefined);
    return () => controller.abort();
  }, []);
  return season;
}

function Squad({ detail, onSelect }: { detail: TeamDetail; onSelect: (player: Player) => void }) {
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
                  <span className="club-player">
                    <button type="button" className="club-player-name" onClick={() => onSelect(p)}>
                      {p.name}
                    </button>
                  </span>
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
  const season = useLatestSeason();
  const [selected, setSelected] = useState<Player | null>(null);
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
        <Squad detail={detail} onSelect={setSelected} />
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
      {selected && (
        <PlayerFlyout player={selected} teamName={team.name} season={season} inMatch={false} onClose={() => setSelected(null)} />
      )}
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
