import type { components } from '../api/schema';
import { positions, type PlayerPosition } from '../teams/teamDraft';

/** A squad player, as a team's detail lists them. */
export type SquadPlayer = components['schemas']['PlayerSummary'];
/** A matchday sheet as the API takes it. */
export type MatchdaySheet = components['schemas']['MatchdaySheet'];

/** The most substitutes on a sheet (ADR-032, MatchdaySheetRules.MaxSubstitutes). */
export const maxSubstitutes = 7;

/** The size of a starting eleven. */
export const elevenSize = 11;

/** A player's part in one match. Anyone not in the map is left out. */
export type Role = 'starter' | 'bench';

/** The fan's picks for one side, by player ID. */
export type Picks = Readonly<Record<string, Role>>;

/** One side's sheet as the fan is building it (ADR-054). */
export interface SideSheet {
  /** On by default: the engine picks the line-up and no sheet is sent. */
  enginePicks: boolean;
  picks: Picks;
}

/** A side's starting state: the engine picks. */
export const engineSheet: SideSheet = { enginePicks: true, picks: {} };

/** Gives a player a role, or takes them off the sheet with `out`. */
export function setRole(picks: Picks, playerId: string, role: Role | 'out'): Picks {
  const next: Record<string, Role> = { ...picks };
  if (role === 'out') delete next[playerId];
  else next[playerId] = role;
  return next;
}

/** The squad grouped by position, in squad order. Positions with no players are left out. */
export function groupByPosition(squad: readonly SquadPlayer[]): { position: PlayerPosition; players: SquadPlayer[] }[] {
  return positions
    .map((position) => ({ position, players: squad.filter((p) => p.position === position) }))
    .filter((g) => g.players.length > 0);
}

/** The live counts and problems of a sheet. */
export interface SheetCheck {
  starters: number;
  goalkeepers: number;
  bench: number;
  /** Derived from the starters' positions, such as `4-4-2`; null until there are eleven with one goalkeeper. */
  formation: string | null;
  problems: string[];
  ok: boolean;
}

function inSquad(picks: Picks, squad: readonly SquadPlayer[], role: Role): SquadPlayer[] {
  return squad.filter((p) => picks[p.id] === role);
}

/**
 * The formation of an eleven: defenders, midfielders and forwards, outfield only (`4-3-3`). Null unless it's eleven
 * players with exactly one goalkeeper.
 */
export function formation(starters: readonly SquadPlayer[]): string | null {
  const count = (position: PlayerPosition) => starters.filter((p) => p.position === position).length;
  if (starters.length !== elevenSize || count('Goalkeeper') !== 1) return null;
  return [count('Defender'), count('Midfielder'), count('Forward')].join('-');
}

/** Checks a sheet against ADR-032's rules: exactly 11 starters with exactly 1 goalkeeper, and 0 to 7 on the bench. */
export function checkSheet(picks: Picks, squad: readonly SquadPlayer[], maxBench = maxSubstitutes): SheetCheck {
  const starters = inSquad(picks, squad, 'starter');
  const bench = inSquad(picks, squad, 'bench');
  const goalkeepers = starters.filter((p) => p.position === 'Goalkeeper').length;
  const problems: string[] = [];
  if (starters.length !== elevenSize) problems.push(`Pick exactly ${elevenSize} starters (${starters.length} so far).`);
  if (goalkeepers !== 1) problems.push(`Start exactly one goalkeeper (${goalkeepers} picked).`);
  if (bench.length > maxBench) problems.push(`The bench takes up to ${maxBench} players (${bench.length} picked).`);
  return {
    starters: starters.length,
    goalkeepers,
    bench: bench.length,
    formation: formation(starters),
    problems,
    ok: problems.length === 0,
  };
}

/**
 * The sheet the API takes: player IDs in squad order, with the goalkeeper first in the eleven. Picks for players no
 * longer in the squad are dropped.
 */
export function sheetFromPicks(picks: Picks, squad: readonly SquadPlayer[]): MatchdaySheet {
  const starters = inSquad(picks, squad, 'starter');
  const keeperFirst = [...starters.filter((p) => p.position === 'Goalkeeper'), ...starters.filter((p) => p.position !== 'Goalkeeper')];
  return { startingEleven: keeperFirst.map((p) => p.id), substitutes: inSquad(picks, squad, 'bench').map((p) => p.id) };
}

/** The sheet to send for a side: null when the engine picks. */
export function sheetToSend(side: SideSheet, squad: readonly SquadPlayer[] | undefined): MatchdaySheet | null {
  return side.enginePicks || !squad ? null : sheetFromPicks(side.picks, squad);
}

/** The shape auto-pick aims for: 1-4-4-2, falling back to whoever's left when a position is short. */
const autoShape: readonly [PlayerPosition, number][] = [
  ['Goalkeeper', 1],
  ['Defender', 4],
  ['Midfielder', 4],
  ['Forward', 2],
];

const byShirt = (a: SquadPlayer, b: SquadPlayer) => a.shirtNumber - b.shirtNumber;

/**
 * A valid sheet in one click: a 4-4-2 by lowest shirt number in each position, filling any short position from the
 * remaining outfield players, then a bench of up to {@link maxSubstitutes} (a spare goalkeeper first). Deterministic,
 * so the same squad always gets the same sheet. A squad with no goalkeeper or fewer than 11 players gets what fits.
 */
export function autoPick(squad: readonly SquadPlayer[], maxBench = maxSubstitutes): Picks {
  const sorted = [...squad].sort(byShirt);
  const chosen = new Set<string>();
  for (const [position, wanted] of autoShape) {
    for (const p of sorted.filter((s) => s.position === position).slice(0, wanted)) chosen.add(p.id);
  }
  for (const p of sorted) {
    if (chosen.size >= elevenSize) break;
    if (p.position !== 'Goalkeeper') chosen.add(p.id);
  }

  const rest = sorted.filter((p) => !chosen.has(p.id));
  const bench = [...rest.filter((p) => p.position === 'Goalkeeper').slice(0, 1), ...rest.filter((p) => p.position !== 'Goalkeeper')]
    .slice(0, maxBench);

  const picks: Record<string, Role> = {};
  for (const id of chosen) picks[id] = 'starter';
  for (const p of bench) picks[p.id] = 'bench';
  return picks;
}
