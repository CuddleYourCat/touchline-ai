import { MatchCard } from '../matches/MatchCard';
import { useMatches, type MatchSummary } from '../matches/useMatches';

const sections: { status: MatchSummary['status']; title: string }[] = [
  { status: 'Live', title: 'Live' },
  { status: 'FullTime', title: 'Full time' },
  { status: 'Scheduled', title: 'Scheduled' },
];

const knownStatuses = new Set<string>(sections.map((s) => s.status));

/** The matches in each section, in display order; a status the page doesn't know goes in "Other". */
function groupMatches(matches: MatchSummary[]): { key: string; title: string; matches: MatchSummary[] }[] {
  return [
    ...sections.map(({ status, title }) => ({ key: status, title, matches: matches.filter((m) => m.status === status) })),
    { key: 'Other', title: 'Other', matches: matches.filter((m) => !knownStatuses.has(m.status)) },
  ].filter((group) => group.matches.length > 0);
}

/** `/`: the list of live, finished and scheduled matches, refreshed while the page is visible. */
export function HomePage() {
  const { state, retry } = useMatches();

  if (state.kind === 'loading') {
    return (
      <section className="panel" aria-busy="true">
        <h1>Matches</h1>
        <p className="muted">Loading matches...</p>
      </section>
    );
  }

  if (state.kind === 'error') {
    return (
      <section className="panel" role="alert">
        <h1>Matches</h1>
        <p>We couldn't load the matches just now.</p>
        <button type="button" className="button" onClick={retry}>
          Try again
        </button>
      </section>
    );
  }

  const groups = groupMatches(state.matches);

  if (groups.length === 0) {
    return (
      <section className="panel">
        <h1>Matches</h1>
        <p className="muted">No matches yet. The demo match kicks off as soon as the service starts.</p>
      </section>
    );
  }

  return (
    <div className="match-list">
      <h1>Matches</h1>
      {groups.map(({ key, title, matches }) => (
        <section key={key} aria-label={title} className="match-section">
          <h2>{title}</h2>
          <ul className="match-grid">
            {matches.map((match) => (
              <li key={match.id}>
                <MatchCard match={match} />
              </li>
            ))}
          </ul>
        </section>
      ))}
    </div>
  );
}
