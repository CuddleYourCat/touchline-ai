import { describe, expect, it } from 'vitest';
import { detail, options } from '../test/teamFixtures';
import {
  checkSquad,
  draftFromForm,
  fieldLabel,
  formFromDetail,
  nameCheckDelay,
  namedFields,
  namesNeedingCheck,
  newTeamForm,
  refusedName,
  starterSquad,
  verdictKey,
  type NameVerdict,
  type PlayerRow,
} from './teamDraft';

describe('starterSquad and newTeamForm', () => {
  it('starts with 16 players that already meet every rule except names', () => {
    const rows = starterSquad(options());
    const check = checkSquad(rows, options());

    expect(rows).toHaveLength(16);
    expect(check.chips.filter((c) => !c.ok).map((c) => c.id)).toEqual(['names']);
    expect(rows.map((r) => r.shirtNumber).slice(0, 2)).toEqual(['1', '13']);
    expect(new Set(rows.map((r) => r.rowKey)).size).toBe(16);
  });

  it('gives a new team a crest with an emblem and a manager style from the options', () => {
    const form = newTeamForm(options());

    expect(form.crest).toMatchObject({ shape: 'Shield', emblem: 'Star', initials: '' });
    expect(form.manager.style).toBe('Balanced');
  });
});

describe('formFromDetail and draftFromForm', () => {
  it('keeps every player ID through a round trip, so history follows a rename', () => {
    const form = formFromDetail(detail());
    form.squad[0] = { ...(form.squad[0] as PlayerRow), name: '  Arlo Renamed  ' };

    const draft = draftFromForm(form);

    expect(draft.squad.map((p) => p.id)).toEqual(detail().squad.map((p) => p.id));
    expect(draft.squad[0]).toEqual({ id: 'fan-harbour-town-p1', name: 'Arlo Renamed', shirtNumber: 1, archetype: 'ShotStopper', preferredFoot: 'Right' });
    expect(draft.manager).toEqual({ name: 'Rory Calloway', style: 'Balanced' });
  });

  it('leaves the ID out for a new player, and sends unreadable values for the server to name', () => {
    const form = newTeamForm(options());
    form.squad[0] = { ...(form.squad[0] as PlayerRow), shirtNumber: 'x', archetype: '' };

    const player = draftFromForm(form).squad[0];

    expect(player).not.toHaveProperty('id');
    expect(player).toMatchObject({ shirtNumber: 0, archetype: null });
  });

  it('turns a built-in player with no archetype into a row that needs a type', () => {
    const d = detail();
    d.squad[0] = { ...d.squad[0]!, archetype: null };

    expect(formFromDetail(d).squad[0]?.archetype).toBe('');
  });
});

describe('checkSquad', () => {
  const named = () => formFromDetail(detail()).squad;

  it('passes a full, valid squad', () => {
    const check = checkSquad(named(), options());

    expect(check.ok).toBe(true);
    expect(check.chips.map((c) => c.label)).toEqual([
      '16 players (14-25)',
      'GK 2/2+',
      'DEF 5/4+',
      'MID 5/3+',
      'FWD 4/3+',
      'Numbers 1-99, no repeats',
      'Every player named',
      'Every player has a type',
    ]);
  });

  it('flags too few players and a short position', () => {
    const rows = named().filter((r) => r.archetype !== 'ShotStopper').slice(0, 13);
    const check = checkSquad(rows, options());

    expect(check.ok).toBe(false);
    expect(check.problems).toContain('A squad has 14 to 25 players.');
    expect(check.problems).toContain('A squad needs at least 2 goalkeepers; this one has 1.');
  });

  it('flags repeated and out-of-range shirt numbers', () => {
    const rows = named();
    rows[1] = { ...rows[1]!, shirtNumber: '1' };
    expect(checkSquad(rows, options()).chips.find((c) => c.id === 'numbers')?.ok).toBe(false);

    rows[1] = { ...rows[1]!, shirtNumber: '100' };
    expect(checkSquad(rows, options()).chips.find((c) => c.id === 'numbers')?.ok).toBe(false);
  });
});

describe('name checks', () => {
  it('lists every non-empty name with the path the server uses', () => {
    const form = formFromDetail(detail());

    const fields = namedFields(form);

    expect(fields.slice(0, 5)).toEqual([
      { path: 'name', kind: 'Team', name: 'Harbour Town' },
      { path: 'shortName', kind: 'Abbreviation', name: 'HAR' },
      { path: 'venue', kind: 'Venue', name: 'Harbour Town Ground' },
      { path: 'crest.initials', kind: 'Abbreviation', name: 'HT' },
      { path: 'manager.name', kind: 'Manager', name: 'Rory Calloway' },
    ]);
    expect(fields[5]).toEqual({ path: 'squad[0].name', kind: 'Player', name: 'Arlo Testwell' });
  });

  it('sends only names not yet checked, once each, and at most 30', () => {
    const form = formFromDetail(detail());
    form.squad = [...form.squad, ...form.squad.map((r) => ({ ...r, name: `${r.name} Junior` }))];
    const checked = new Map<string, NameVerdict>([[verdictKey('Team', 'Harbour Town'), { kind: 'Team', name: 'Harbour Town', allowed: true, reason: null }]]);

    const pending = namesNeedingCheck(form, checked);

    expect(pending).toHaveLength(30);
    expect(pending).not.toContainEqual({ kind: 'Team', name: 'Harbour Town' });
  });

  it('reports a refused name against its field, and only while the field still holds it', () => {
    const form = formFromDetail(detail());
    const checked = new Map<string, NameVerdict>([
      [verdictKey('Venue', 'Harbour Town Ground'), { kind: 'Venue', name: 'Harbour Town Ground', allowed: false, reason: 'Too close to a real ground.' }],
    ]);

    expect(refusedName(form, 'venue', checked)).toBe('Too close to a real ground.');
    expect(refusedName({ ...form, venue: 'Quayside Park' }, 'venue', checked)).toBeUndefined();
  });

  it('waits a moment after a blur, and keeps checks at least ten seconds apart', () => {
    expect(nameCheckDelay(1_000, undefined)).toBe(1_500);
    expect(nameCheckDelay(5_000, 1_000)).toBe(6_000);
    expect(nameCheckDelay(20_000, 1_000)).toBe(1_500);
  });
});

describe('fieldLabel', () => {
  it('names server paths in plain words', () => {
    expect(fieldLabel('squad[2].shirtNumber')).toBe('Player 3 shirt number');
    expect(fieldLabel('crest.initials')).toBe('Crest initials');
    expect(fieldLabel('something.new')).toBe('Team');
  });
});
