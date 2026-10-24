import type { components } from '../api/schema';

type Schemas = components['schemas'];

/** A club as listed. */
export type TeamSummary = Schemas['TeamSummary'];
/** A club in full, with its version. */
export type TeamDetail = Schemas['TeamDetail'];
/** A fan team as created or replaced. */
export type TeamDraft = Schemas['TeamDraft'];
/** The builder's choices and rules. */
export type TeamBuilderOptions = Schemas['TeamBuilderOptions'];
/** The squad and sheet rules. */
export type SquadRules = Schemas['SquadRulesView'];
/** A crest's parts. */
export type CrestSpec = Schemas['CrestSpec'];
/** A name kind the guard checks. */
export type NameKind = Schemas['NameKind'];
/** One name to check. */
export type NameToCheck = Schemas['NameToCheck'];
/** The guard's verdict on one name. */
export type NameVerdict = Schemas['NameVerdict'];
/** A broad position. */
export type PlayerPosition = Schemas['PlayerPosition'];
/** A player type. The contract's enum allows null (built-in players have none); a draft always has one. */
export type PlayerArchetype = NonNullable<Schemas['PlayerArchetype']>;
/** Left, right or both. */
export type PreferredFoot = Schemas['PreferredFoot'];

/** The positions, in squad order. */
export const positions: readonly PlayerPosition[] = ['Goalkeeper', 'Defender', 'Midfielder', 'Forward'];

/** Short position labels for chips and counts. */
export const positionShort: Readonly<Record<PlayerPosition, string>> = {
  Goalkeeper: 'GK',
  Defender: 'DEF',
  Midfielder: 'MID',
  Forward: 'FWD',
};

/** Plural position names for headings. */
export const positionPlural: Readonly<Record<PlayerPosition, string>> = {
  Goalkeeper: 'Goalkeepers',
  Defender: 'Defenders',
  Midfielder: 'Midfielders',
  Forward: 'Forwards',
};

/**
 * One row of the squad editor. The shirt number stays a string while it's typed, and `rowKey` is a stable React key
 * that survives reordering and removals. `id` is the service's player ID, kept so a player's history follows them
 * through a rename (ADR-031); it's absent for a new player.
 */
export interface PlayerRow {
  rowKey: string;
  id?: string;
  name: string;
  shirtNumber: string;
  archetype: PlayerArchetype | '';
  preferredFoot: PreferredFoot;
}

/** The team builder's form: a TeamDraft whose squad is still being edited. */
export interface TeamForm extends Omit<TeamDraft, 'squad'> {
  squad: PlayerRow[];
}

let rowCounter = 0;

/** A fresh, unique row key. */
export function newRowKey(): string {
  rowCounter += 1;
  return `row-${rowCounter}`;
}

/** A blank player row. */
export function blankPlayer(shirtNumber = ''): PlayerRow {
  return { rowKey: newRowKey(), name: '', shirtNumber, archetype: '', preferredFoot: 'Right' };
}

/** How many players of each position the starter squad has: 16, inside every default rule. */
const starterCounts: Readonly<Record<PlayerPosition, number>> = { Goalkeeper: 2, Defender: 5, Midfielder: 5, Forward: 4 };

/** Traditional shirt numbers for the starter squad, by position. */
const starterNumbers: Readonly<Record<PlayerPosition, readonly number[]>> = {
  Goalkeeper: [1, 13],
  Defender: [2, 3, 4, 5, 12],
  Midfielder: [6, 7, 8, 10, 14],
  Forward: [9, 11, 15, 16],
};

/**
 * A squad that already meets the rules, with names left blank: two goalkeepers, five defenders, five midfielders and
 * four forwards, cycling through each position's archetypes from the options.
 */
export function starterSquad(options: TeamBuilderOptions): PlayerRow[] {
  const rows: PlayerRow[] = [];
  for (const position of positions) {
    const types = options.archetypes.flatMap((a) => (a.position === position && a.id ? [a.id] : []));
    const numbers = starterNumbers[position];
    for (let i = 0; i < starterCounts[position]; i++) {
      rows.push({
        ...blankPlayer(String(numbers[i] ?? '')),
        archetype: types.length > 0 ? (types[i % types.length] ?? '') : '',
      });
    }
  }
  return rows;
}

/** A new team's form: default colours, a crest, a balanced manager and a starter squad. */
export function newTeamForm(options: TeamBuilderOptions): TeamForm {
  const primary = '#1f6feb';
  const secondary = '#ffffff';
  return {
    name: '',
    shortName: '',
    venue: '',
    colours: { primary, secondary },
    crest: {
      shape: options.crestShapes[0] ?? 'Shield',
      emblem: options.crestEmblems.find((e) => e !== 'None') ?? 'None',
      initials: '',
      primary,
      secondary,
    },
    manager: { name: '', style: options.managerStyles[0]?.id ?? 'Balanced' },
    squad: starterSquad(options),
  };
}

/** An existing team as a form. Player IDs are kept, so an update keeps each player's history. */
export function formFromDetail(detail: TeamDetail): TeamForm {
  const { team, crest, manager, squad } = detail;
  return {
    name: team.name,
    shortName: team.shortName,
    venue: team.venue,
    colours: { ...team.colours },
    crest: { ...crest },
    manager: manager ? { ...manager } : { name: '', style: 'Balanced' },
    squad: squad.map((p) => ({
      rowKey: newRowKey(),
      id: p.id,
      name: p.name,
      shirtNumber: String(p.shirtNumber),
      archetype: p.archetype ?? '',
      preferredFoot: p.preferredFoot,
    })),
  };
}

/** Parses a typed shirt number; NaN when it isn't a whole number. */
export function shirtNumberOf(text: string): number {
  return /^\s*\d{1,3}\s*$/.test(text) ? Number.parseInt(text, 10) : Number.NaN;
}

/**
 * The form as the TeamDraft the API takes. Names are trimmed; an unreadable shirt number is sent as 0 so the server's
 * rule names the row. A row with no archetype is sent as null, which the server rejects with the row's path.
 */
export function draftFromForm(form: TeamForm): TeamDraft {
  return {
    name: form.name.trim(),
    shortName: form.shortName.trim(),
    venue: form.venue.trim(),
    colours: { ...form.colours },
    crest: { ...form.crest, initials: form.crest.initials.trim() },
    manager: { name: form.manager.name.trim(), style: form.manager.style },
    squad: form.squad.map((row) => {
      const number = shirtNumberOf(row.shirtNumber);
      return {
        ...(row.id ? { id: row.id } : {}),
        name: row.name.trim(),
        shirtNumber: Number.isNaN(number) ? 0 : number,
        archetype: row.archetype === '' ? null : row.archetype,
        preferredFoot: row.preferredFoot,
      };
    }),
  };
}

/** One rule chip in the squad editor. */
export interface RuleChip {
  id: string;
  label: string;
  ok: boolean;
}

/** The client's reading of the squad rules. The server is still the authority: it checks again on save. */
export interface SquadCheck {
  chips: RuleChip[];
  problems: string[];
  ok: boolean;
}

/** The position each archetype plays, from the options. */
export function archetypePositions(options: TeamBuilderOptions): ReadonlyMap<PlayerArchetype, PlayerPosition> {
  return new Map(options.archetypes.flatMap((a) => (a.id ? [[a.id, a.position] as const] : [])));
}

/** Checks a squad against the options' rules, mirroring the server's squad rules (ADR-031). */
export function checkSquad(squad: readonly PlayerRow[], options: TeamBuilderOptions): SquadCheck {
  const rules = options.rules;
  const byArchetype = archetypePositions(options);
  const chips: RuleChip[] = [];
  const problems: string[] = [];

  const size = squad.length;
  const sizeOk = size >= rules.minPlayers && size <= rules.maxPlayers;
  chips.push({ id: 'size', label: `${size} players (${rules.minPlayers}-${rules.maxPlayers})`, ok: sizeOk });
  if (!sizeOk) problems.push(`A squad has ${rules.minPlayers} to ${rules.maxPlayers} players.`);

  const counts = new Map<PlayerPosition, number>();
  for (const row of squad) {
    const position = row.archetype === '' ? undefined : byArchetype.get(row.archetype);
    if (position) counts.set(position, (counts.get(position) ?? 0) + 1);
  }
  for (const { position, minimum } of rules.minimumByPosition) {
    const count = counts.get(position) ?? 0;
    const ok = count >= minimum;
    chips.push({ id: `pos-${position}`, label: `${positionShort[position]} ${count}/${minimum}+`, ok });
    if (!ok) problems.push(`A squad needs at least ${minimum} ${positionPlural[position].toLowerCase()}; this one has ${count}.`);
  }

  const seen = new Set<number>();
  let numbersOk = true;
  for (const row of squad) {
    const number = shirtNumberOf(row.shirtNumber);
    if (Number.isNaN(number) || number < rules.minShirtNumber || number > rules.maxShirtNumber || seen.has(number)) {
      numbersOk = false;
    }
    if (!Number.isNaN(number)) seen.add(number);
  }
  chips.push({ id: 'numbers', label: `Numbers ${rules.minShirtNumber}-${rules.maxShirtNumber}, no repeats`, ok: numbersOk });
  if (!numbersOk) problems.push(`Every player needs a different shirt number from ${rules.minShirtNumber} to ${rules.maxShirtNumber}.`);

  const namesOk = squad.every((row) => row.name.trim().length > 0);
  chips.push({ id: 'names', label: 'Every player named', ok: namesOk });
  if (!namesOk) problems.push('Every player needs a name.');

  const typesOk = squad.every((row) => row.archetype !== '');
  chips.push({ id: 'types', label: 'Every player has a type', ok: typesOk });
  if (!typesOk) problems.push('Every player needs a player type.');

  return { chips, problems, ok: problems.length === 0 };
}

/** One free-text name in the form, with the path the server's validation errors use for it. */
export interface NamedField {
  path: string;
  kind: NameKind;
  name: string;
}

/** Every non-empty name in the form, as the name guard sees them (ADR-031 and its review fixes). */
export function namedFields(form: TeamForm): NamedField[] {
  const fields: NamedField[] = [
    { path: 'name', kind: 'Team', name: form.name.trim() },
    { path: 'shortName', kind: 'Abbreviation', name: form.shortName.trim() },
    { path: 'venue', kind: 'Venue', name: form.venue.trim() },
    { path: 'crest.initials', kind: 'Abbreviation', name: form.crest.initials.trim() },
    { path: 'manager.name', kind: 'Manager', name: form.manager.name.trim() },
    ...form.squad.map((row, i) => ({ path: `squad[${i}].name`, kind: 'Player' as const, name: row.name.trim() })),
  ];
  return fields.filter((f) => f.name.length > 0);
}

/** The cache key for a verdict. */
export function verdictKey(kind: NameKind, name: string): string {
  return `${kind}:${name}`;
}

/** The most names one check may send (NameCheckRequest.MaxNames). */
export const maxNamesPerCheck = 30;

/** The distinct names in the form that haven't been checked yet, at most {@link maxNamesPerCheck}. */
export function namesNeedingCheck(form: TeamForm, checked: ReadonlyMap<string, NameVerdict>): NameToCheck[] {
  const pending = new Map<string, NameToCheck>();
  for (const { kind, name } of namedFields(form)) {
    const key = verdictKey(kind, name);
    if (!checked.has(key) && !pending.has(key)) pending.set(key, { kind, name });
  }
  return [...pending.values()].slice(0, maxNamesPerCheck);
}

/** The reason a field's current name was refused, or undefined when it's allowed or not checked yet. */
export function refusedName(form: TeamForm, path: string, checked: ReadonlyMap<string, NameVerdict>): string | undefined {
  const field = namedFields(form).find((f) => f.path === path);
  if (!field) return undefined;
  const verdict = checked.get(verdictKey(field.kind, field.name));
  return verdict && !verdict.allowed ? (verdict.reason ?? "That name isn't allowed.") : undefined;
}

/**
 * How long to wait before the next name check. Checks are batched: a blur waits {@link debounceMs} for more edits,
 * and checks are at least {@link minGapMs} apart, so a fan tabbing through 20 names stays well inside the
 * 10-a-minute limit on the endpoint.
 */
export function nameCheckDelay(now: number, lastCheckAt: number | undefined, debounceMs = 1_500, minGapMs = 10_000): number {
  const earliest = lastCheckAt === undefined ? now : lastCheckAt + minGapMs;
  return Math.max(debounceMs, earliest - now);
}

/** A friendly label for a server field path, for the error summary. */
export function fieldLabel(path: string): string {
  const player = /^squad\[(\d+)\](?:\.(\w+))?$/.exec(path);
  if (player) {
    const row = Number.parseInt(player[1] ?? '0', 10) + 1;
    const part = { name: 'name', shirtNumber: 'shirt number', archetype: 'type', preferredFoot: 'foot', id: 'player' }[player[2] ?? ''] ?? '';
    return `Player ${row}${part ? ` ${part}` : ''}`;
  }
  const labels: Record<string, string> = {
    name: 'Club name',
    shortName: 'Short name',
    venue: 'Ground',
    colours: 'Kit colours',
    'colours.primary': 'Main kit colour',
    'colours.secondary': 'Trim kit colour',
    crest: 'Crest',
    'crest.shape': 'Crest shape',
    'crest.emblem': 'Crest emblem',
    'crest.initials': 'Crest initials',
    'crest.primary': 'Crest main colour',
    'crest.secondary': 'Crest trim colour',
    manager: 'Manager',
    'manager.name': 'Manager name',
    'manager.style': 'Manager style',
    squad: 'Squad',
  };
  return labels[path] ?? 'Team';
}
