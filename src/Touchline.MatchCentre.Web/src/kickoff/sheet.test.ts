import { describe, expect, it } from 'vitest';
import { squad } from '../test/teamFixtures';
import { autoPick, checkSheet, engineSheet, formation, groupByPosition, setRole, sheetFromPicks, sheetToSend, type Picks } from './sheet';

const players = squad('fan-a');
const id = (n: number) => `fan-a-p${n}`;

function picksOf(starters: number[], bench: number[] = []): Picks {
  let picks: Picks = {};
  for (const n of starters) picks = setRole(picks, id(n), 'starter');
  for (const n of bench) picks = setRole(picks, id(n), 'bench');
  return picks;
}

describe('autoPick', () => {
  it('picks a valid 4-4-2 by lowest shirt number, with a spare goalkeeper first on the bench', () => {
    const picks = autoPick(players);
    const sheet = sheetFromPicks(picks, players);

    expect(checkSheet(picks, players)).toMatchObject({ ok: true, starters: 11, goalkeepers: 1, bench: 5, formation: '4-4-2' });
    expect(sheet.startingEleven).toEqual([1, 3, 4, 5, 6, 8, 9, 10, 11, 13, 14].map(id));
    expect(sheet.substitutes).toEqual([2, 7, 12, 15, 16].map(id));
  });

  it('fills a short position from the other outfield players, and caps the bench', () => {
    const fewForwards = players.filter((p) => p.position !== 'Forward' || p.shirtNumber === 13);
    const picks = autoPick(fewForwards, 2);
    const check = checkSheet(picks, fewForwards);

    expect(check.starters).toBe(11);
    expect(check.goalkeepers).toBe(1);
    expect(check.formation).toBe('5-4-1');
    expect(sheetFromPicks(picks, fewForwards).substitutes).toEqual([id(2), id(12)]);
  });

  it('is deterministic', () => {
    expect(autoPick([...players].reverse())).toEqual(autoPick(players));
  });
});

describe('checkSheet', () => {
  it('counts live and explains what is missing', () => {
    const check = checkSheet(picksOf([3, 4, 5]), players);

    expect(check).toMatchObject({ starters: 3, goalkeepers: 0, bench: 0, formation: null, ok: false });
    expect(check.problems).toEqual(['Pick exactly 11 starters (3 so far).', 'Start exactly one goalkeeper (0 picked).']);
  });

  it('refuses two goalkeepers and an oversized bench', () => {
    const picks = picksOf([1, 2, 3, 4, 5, 6, 8, 9, 10, 13, 14], [7, 11, 12, 15, 16, 17, 18, 19]);
    const check = checkSheet(picks, players, 3);

    expect(check.goalkeepers).toBe(2);
    expect(check.problems).toContain('Start exactly one goalkeeper (2 picked).');
    expect(check.problems).toContain('The bench takes up to 3 players (5 picked).');
  });
});

describe('formation', () => {
  it('derives defenders-midfielders-forwards from an eleven with one goalkeeper', () => {
    const eleven = players.filter((p) => [1, 3, 4, 5, 8, 9, 10, 11, 13, 14, 15].includes(p.shirtNumber));
    expect(formation(eleven)).toBe('3-4-3');
    expect(formation(eleven.slice(1))).toBeNull();
  });
});

describe('setRole, groupByPosition and sheetToSend', () => {
  it('moves a player between roles and takes them out', () => {
    let picks = setRole({}, id(1), 'starter');
    picks = setRole(picks, id(1), 'bench');
    expect(picks).toEqual({ [id(1)]: 'bench' });
    expect(setRole(picks, id(1), 'out')).toEqual({});
  });

  it('groups by position, in squad order, skipping empty positions', () => {
    const groups = groupByPosition(players.filter((p) => p.position !== 'Midfielder'));
    expect(groups.map((g) => [g.position, g.players.length])).toEqual([
      ['Goalkeeper', 2],
      ['Defender', 5],
      ['Forward', 4],
    ]);
  });

  it('sends no sheet while the engine picks, and puts the goalkeeper first otherwise', () => {
    expect(sheetToSend(engineSheet, players)).toBeNull();
    const sent = sheetToSend({ enginePicks: false, picks: picksOf([3, 1]) }, players);
    expect(sent?.startingEleven).toEqual([id(1), id(3)]);
  });

  it('drops picks for players no longer in the squad', () => {
    expect(sheetFromPicks({ 'fan-a-p99': 'starter' }, players).startingEleven).toEqual([]);
  });
});
