import { useId, useRef, useState, type FormEvent } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { describeWait } from '../api/problems';
import {
  autoPick,
  checkSheet,
  engineSheet,
  groupByPosition,
  setRole,
  sheetToSend,
  type Role,
  type SideSheet,
} from '../kickoff/sheet';
import { errorsBySide, startMatch, type StartOutcome } from '../kickoff/startMatch';
import { isTeamId } from '../teams/editKeys';
import { positionPlural, type TeamSummary } from '../teams/teamDraft';
import { useTeamDetail, useTeams, type DetailState } from '../teams/useTeams';

type Side = 'home' | 'away';

interface SidePickerProps {
  side: Side;
  teams: TeamSummary[];
  teamId: string;
  onTeam: (teamId: string) => void;
  sheet: SideSheet;
  onSheet: (sheet: SideSheet) => void;
  detail: DetailState;
  onRetryDetail: () => void;
  errors: string[];
}

/** One side of the kick-off form: the team, and its sheet when the fan picks it (ADR-054). */
function SidePicker({ side, teams, teamId, onTeam, sheet, onSheet, detail, onRetryDetail, errors }: SidePickerProps) {
  const id = useId();
  const title = side === 'home' ? 'Home' : 'Away';
  const squad = detail.kind === 'ready' ? detail.detail.squad : undefined;
  const check = squad ? checkSheet(sheet.picks, squad) : undefined;
  const team = teams.find((t) => t.id === teamId);

  return (
    <fieldset className="panel kickoff-side" aria-describedby={errors.length > 0 ? `${id}-errors` : undefined}>
      <legend className="builder-legend">{title}</legend>
      <label className="builder-field">
        <span className="viewer-label">{title} team</span>
        <select className="select" value={teamId} onChange={(e) => onTeam(e.target.value)}>
          <option value="">Choose a team</option>
          {teams.map((t) => (
            <option key={t.id} value={t.id}>
              {t.name}
              {t.origin === 'Fan' ? ' (fan-made)' : ''}
            </option>
          ))}
        </select>
      </label>
      {team && (
        <div className="kickoff-team">
          <img src={team.crestUrl} alt="" width={40} height={40} />
          <span>{team.venue}</span>
          {team.origin === 'Fan' && <span className="pill">fan-made</span>}
        </div>
      )}

      {teamId && (
        <label className="toggle">
          <input type="checkbox" checked={sheet.enginePicks} onChange={(e) => onSheet({ enginePicks: e.target.checked, picks: sheet.picks })} />
          <span>Engine picks the line-up</span>
        </label>
      )}

      {teamId && !sheet.enginePicks && (
        <div className="sheet">
          {detail.kind === 'loading' && <p className="muted">Loading the squad...</p>}
          {(detail.kind === 'error' || detail.kind === 'notFound') && (
            <div role="alert">
              <p>{detail.kind === 'notFound' ? "That team doesn't exist any more." : "We couldn't load the squad just now."}</p>
              {detail.kind === 'error' && (
                <button type="button" className="button" onClick={onRetryDetail}>
                  Try again
                </button>
              )}
            </div>
          )}
          {squad && check && (
            <>
              <div className="sheet-counts" aria-live="polite">
                <span className={check.starters === 11 ? 'pill pill-ok' : 'pill pill-bad'}>XI {check.starters}/11</span>
                <span className={check.goalkeepers === 1 ? 'pill pill-ok' : 'pill pill-bad'}>GK {check.goalkeepers}/1</span>
                <span className={check.bench <= 7 ? 'pill pill-ok' : 'pill pill-bad'}>Bench {check.bench}/7</span>
                <span className="pill">{check.formation ?? 'Formation: pick 11'}</span>
              </div>
              <button type="button" className="button button-ghost button-small" onClick={() => onSheet({ enginePicks: false, picks: autoPick(squad) })}>
                Auto-pick
              </button>
              {groupByPosition(squad).map(({ position, players }) => (
                <fieldset key={position} className="sheet-group">
                  <legend className="viewer-label">{positionPlural[position]}</legend>
                  <ul className="sheet-list">
                    {players.map((p) => (
                      <li key={p.id} className="sheet-player">
                        <label htmlFor={`${id}-${p.id}`}>
                          <span className="data">{p.shirtNumber}</span> {p.name}
                        </label>
                        <select
                          id={`${id}-${p.id}`}
                          className="select"
                          value={sheet.picks[p.id] ?? 'out'}
                          onChange={(e) => onSheet({ enginePicks: false, picks: setRole(sheet.picks, p.id, e.target.value as Role | 'out') })}
                        >
                          <option value="out">Not picked</option>
                          <option value="starter">Starting</option>
                          <option value="bench">Bench</option>
                        </select>
                      </li>
                    ))}
                  </ul>
                </fieldset>
              ))}
              {check.problems.length > 0 && (
                <ul className="sheet-problems">
                  {check.problems.map((p) => (
                    <li key={p}>{p}</li>
                  ))}
                </ul>
              )}
            </>
          )}
        </div>
      )}
      {errors.length > 0 && (
        <ul id={`${id}-errors`} className="field-error sheet-problems" role="alert">
          {errors.map((e, i) => (
            <li key={i}>{e}</li>
          ))}
        </ul>
      )}
    </fieldset>
  );
}

/** The message for an outcome that keeps the fan on this page. */
function outcomeMessage(outcome: StartOutcome): { tone: 'quiet' | 'alert'; text: string } | undefined {
  switch (outcome.kind) {
    case 'accepted':
      return undefined;
    case 'pipelineOff':
      return { tone: 'quiet', text: 'Fan matches go live when the match pipeline is switched on. Your teams and line-up are valid.' };
    case 'busy':
      return {
        tone: 'alert',
        text: `Too many fan matches right now. Try again in ${describeWait(outcome.retryAfterSeconds ?? 60)}.`,
      };
    case 'sameTeam':
      return { tone: 'alert', text: "A team can't play itself. Pick two different teams." };
    case 'unknownTeam':
      return { tone: 'alert', text: "One of those teams doesn't exist any more. Pick another." };
    case 'invalid':
      return { tone: 'alert', text: 'The line-up needs changing. The problems are marked on each side.' };
    case 'error':
      return { tone: 'alert', text: "We couldn't reach the Match Centre just now. Your picks are still here, so try again." };
  }
}

/** A team ID from the query string, if it's well formed and one of the listed teams. Only IDs, never keys. */
function initialTeam(param: string | null, teams: TeamSummary[]): string {
  return param && isTeamId(param) && teams.some((t) => t.id === param) ? param : '';
}

function KickoffForm({ teams, initialHome, initialAway }: { teams: TeamSummary[]; initialHome: string; initialAway: string }) {
  const navigate = useNavigate();
  const [home, setHome] = useState(initialHome);
  const [away, setAway] = useState(initialAway);
  const [homeSheet, setHomeSheet] = useState<SideSheet>(engineSheet);
  const [awaySheet, setAwaySheet] = useState<SideSheet>(engineSheet);
  const homeDetail = useTeamDetail(home || undefined);
  const awayDetail = useTeamDetail(away || undefined);
  const [outcome, setOutcome] = useState<StartOutcome>();
  const [clientProblem, setClientProblem] = useState<string>();
  const [inFlight, setInFlight] = useState(false);
  const submitting = useRef(false);
  // One Idempotency-Key per intended match: kept for a retry after a failure that may not have reached the
  // service, and dropped when the teams or sheets change or the service has answered for good.
  const idempotencyKey = useRef<string | undefined>(undefined);
  const newIntent = () => {
    idempotencyKey.current = undefined;
  };
  const changeSheet = (side: Side, sheet: SideSheet) => {
    newIntent();
    if (side === 'home') setHomeSheet(sheet);
    else setAwaySheet(sheet);
  };

  const squadOf = (d: DetailState) => (d.kind === 'ready' ? d.detail.squad : undefined);
  const sideErrors = outcome?.kind === 'invalid' ? errorsBySide(outcome.errors) : { home: [], away: [], general: [] };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (submitting.current) return;
    setClientProblem(undefined);
    if (!home || !away) {
      setClientProblem('Pick a home team and an away team.');
      return;
    }
    if (home === away) {
      setOutcome({ kind: 'sameTeam' });
      return;
    }
    for (const [sheet, detail, label] of [
      [homeSheet, homeDetail.state, 'home'],
      [awaySheet, awayDetail.state, 'away'],
    ] as const) {
      if (sheet.enginePicks) continue;
      const squad = squadOf(detail);
      if (!squad || !checkSheet(sheet.picks, squad).ok) {
        setClientProblem(`The ${label} line-up isn't ready yet: pick 11 starters with one goalkeeper, or let the engine pick.`);
        return;
      }
    }

    submitting.current = true;
    setInFlight(true);
    setOutcome(undefined);
    idempotencyKey.current ??= crypto.randomUUID();
    const result = await startMatch(
      {
        homeTeamId: home,
        awayTeamId: away,
        home: sheetToSend(homeSheet, squadOf(homeDetail.state)),
        away: sheetToSend(awaySheet, squadOf(awayDetail.state)),
      },
      idempotencyKey.current,
    );
    submitting.current = false;
    setInFlight(false);
    // Only an `error` (a 5xx or a dropped response) may not have been settled, so only it keeps the key for a retry.
    if (result.kind !== 'error') newIntent();
    if (result.kind === 'accepted') {
      void navigate(`/matches/${encodeURIComponent(result.matchId)}`);
      return;
    }
    setOutcome(result);
  };

  const message = outcome ? outcomeMessage(outcome) : undefined;
  const changeTeam = (side: Side, teamId: string) => {
    newIntent();
    if (side === 'home') {
      setHome(teamId);
      setHomeSheet(engineSheet);
    } else {
      setAway(teamId);
      setAwaySheet(engineSheet);
    }
    setOutcome(undefined);
  };

  return (
    <form className="kickoff" onSubmit={(e) => void submit(e)} noValidate aria-label="Kick off a match">
      <div className="kickoff-sides">
        <SidePicker
          side="home"
          teams={teams}
          teamId={home}
          onTeam={(teamId) => changeTeam('home', teamId)}
          sheet={homeSheet}
          onSheet={(sheet) => changeSheet('home', sheet)}
          detail={homeDetail.state}
          onRetryDetail={homeDetail.retry}
          errors={sideErrors.home}
        />
        <SidePicker
          side="away"
          teams={teams}
          teamId={away}
          onTeam={(teamId) => changeTeam('away', teamId)}
          sheet={awaySheet}
          onSheet={(sheet) => changeSheet('away', sheet)}
          detail={awayDetail.state}
          onRetryDetail={awayDetail.retry}
          errors={sideErrors.away}
        />
      </div>
      <div className="kickoff-actions">
        <button type="submit" className="button" disabled={inFlight}>
          {inFlight ? 'Starting...' : 'Kick off'}
        </button>
        <span className="tag">An exhibition: it never counts towards a season.</span>
      </div>
      <div role="status">{message?.tone === 'quiet' && <p className="banner banner-quiet">{message.text}</p>}</div>
      {(message?.tone === 'alert' || clientProblem || sideErrors.general.length > 0) && (
        <div role="alert" className="banner builder-alert">
          {clientProblem && <p>{clientProblem}</p>}
          {message?.tone === 'alert' && <p>{message.text}</p>}
          {sideErrors.general.map((e, i) => (
            <p key={i}>{e}</p>
          ))}
        </div>
      )}
    </form>
  );
}

/** `/kickoff`: pick two teams and, optionally, their line-ups, then start a synthetic exhibition (ADR-032, ADR-054). */
export function KickoffPage() {
  const { state, retry } = useTeams();
  const [params] = useSearchParams();

  return (
    <div className="kickoff-page">
      <section className="panel">
        <h1>Kick off</h1>
        <p className="muted">
          Pick two teams and start a synthetic match. Leave the line-ups to the engine, or pick the starting eleven and the bench
          yourself. <Link to="/teams/new">Build your own team</Link>.
        </p>
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
        <KickoffForm
          teams={state.teams}
          initialHome={initialTeam(params.get('home'), state.teams)}
          initialAway={initialTeam(params.get('away'), state.teams)}
        />
      )}
    </div>
  );
}
