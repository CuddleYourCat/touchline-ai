import type { SquadPlayer } from '../kickoff/sheet';
import type { PlayerArchetype, PlayerPosition, TeamBuilderOptions, TeamDetail, TeamSummary } from '../teams/teamDraft';

/** Builder options shaped like the service's: archetypes by position and the ADR-031 rules. */
export function options(): TeamBuilderOptions {
  const archetype = (id: PlayerArchetype, position: PlayerPosition, label: string) => ({ id, position, label, description: `${label}.` });
  return {
    archetypes: [
      archetype('ShotStopper', 'Goalkeeper', 'Shot stopper'),
      archetype('SweeperKeeper', 'Goalkeeper', 'Sweeper keeper'),
      archetype('Stopper', 'Defender', 'Stopper'),
      archetype('BallPlayingDefender', 'Defender', 'Ball-playing defender'),
      archetype('AttackingFullBack', 'Defender', 'Attacking full-back'),
      archetype('BallWinner', 'Midfielder', 'Ball winner'),
      archetype('DeepPlaymaker', 'Midfielder', 'Deep playmaker'),
      archetype('BoxToBox', 'Midfielder', 'Box to box'),
      archetype('Winger', 'Forward', 'Winger'),
      archetype('TargetForward', 'Forward', 'Target forward'),
      archetype('Poacher', 'Forward', 'Poacher'),
    ],
    managerStyles: [
      { id: 'Balanced', label: 'Balanced', description: 'A bit of everything.' },
      { id: 'HighPress', label: 'High press', description: 'Wins it back high.' },
    ],
    crestShapes: ['Shield', 'Round', 'Diamond', 'Banner', 'Hexagon'],
    crestEmblems: ['None', 'Star', 'Oak', 'Anchor'],
    rules: {
      minPlayers: 14,
      maxPlayers: 25,
      minShirtNumber: 1,
      maxShirtNumber: 99,
      minimumByPosition: [
        { position: 'Goalkeeper', minimum: 2 },
        { position: 'Defender', minimum: 4 },
        { position: 'Midfielder', minimum: 3 },
        { position: 'Forward', minimum: 3 },
      ],
      maxSubstitutes: 7,
      maxNameLength: 30,
    },
  };
}

const firstNames = ['Arlo', 'Bram', 'Cass', 'Dex', 'Eli', 'Finn', 'Gus', 'Hal', 'Ivo', 'Jem', 'Kit', 'Lew', 'Max', 'Ned', 'Oz', 'Pip'];

/** A 16-player squad: 2 GK, 5 DEF, 5 MID, 4 FWD, numbered 1 to 16. */
export function squad(teamId = 'fan-harbour-town'): SquadPlayer[] {
  const plan: [PlayerPosition, PlayerArchetype][] = [
    ['Goalkeeper', 'ShotStopper'],
    ['Goalkeeper', 'SweeperKeeper'],
    ['Defender', 'Stopper'],
    ['Defender', 'Stopper'],
    ['Defender', 'BallPlayingDefender'],
    ['Defender', 'AttackingFullBack'],
    ['Defender', 'AttackingFullBack'],
    ['Midfielder', 'BallWinner'],
    ['Midfielder', 'DeepPlaymaker'],
    ['Midfielder', 'BoxToBox'],
    ['Midfielder', 'BoxToBox'],
    ['Midfielder', 'BallWinner'],
    ['Forward', 'Winger'],
    ['Forward', 'Winger'],
    ['Forward', 'TargetForward'],
    ['Forward', 'Poacher'],
  ];
  return plan.map(([position, archetype], i) => ({
    id: `${teamId}-p${i + 1}`,
    name: `${firstNames[i] ?? 'Sam'} Testwell`,
    shirtNumber: i + 1,
    position,
    teamId,
    archetype,
    preferredFoot: 'Right',
  }));
}

/** A club as listed. */
export function summary(id: string, name: string, origin: TeamSummary['origin'] = 'Fan'): TeamSummary {
  return {
    id,
    name,
    shortName: name.slice(0, 3).toUpperCase(),
    venue: `${name} Ground`,
    colours: { primary: '#123456', secondary: '#ffffff' },
    origin,
    crestUrl: `/api/teams/${id}/crest.svg`,
  };
}

/** A fan team in full, at a version. */
export function detail(id = 'fan-harbour-town', version = 3): TeamDetail {
  return {
    team: summary(id, 'Harbour Town'),
    manager: { name: 'Rory Calloway', style: 'Balanced' },
    crest: { shape: 'Shield', emblem: 'Anchor', initials: 'HT', primary: '#123456', secondary: '#ffffff' },
    squad: squad(id),
    version,
  };
}

/** openapi-fetch's shape for a success. */
export const ok = (data: unknown, status = 200, headers: Record<string, string> = {}) => ({
  data,
  error: undefined,
  response: new Response(null, { status, headers }),
});

/** openapi-fetch's shape for a failure. */
export const fail = (status: number, error: unknown = { detail: 'secret stack trace' }, headers: Record<string, string> = {}) => ({
  data: undefined,
  error,
  response: new Response(null, { status, headers }),
});
