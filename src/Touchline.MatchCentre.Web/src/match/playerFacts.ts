import type { MomentView } from '../live/matchStreamReducer';

type Clock = MomentView['clock'];

/** Something a player did that the line-ups card, the timeline and the player flyout show (ADR-068). */
export type PlayerFactKind = 'goal' | 'assist' | 'yellow' | 'red' | 'subOn' | 'subOff';

export interface PlayerFact {
  kind: PlayerFactKind;
  /** The moment it came from, so a click can replay from it (ADR-067). */
  momentId: string;
  sequence: number;
  clock: Clock;
}

/** Each player's facts, by player ID, in match order. A player with none has no entry. */
export type PlayerFacts = Record<string, PlayerFact[]>;

/**
 * The football minute for a clock reading, as a scoreboard shows it: `67:12` is the 68th minute, and added time
 * reads `45+2'` or `90+3'`. The second half's clock starts at 45:00 (ADR-023).
 */
export function footballMinute(clock: Clock): string {
  const minute = clock.minute + 1;
  const end = clock.period === 'FirstHalf' ? 45 : 90;
  return minute > end ? `${end}+${minute - end}'` : `${minute}'`;
}

/**
 * Who scored, assisted, was booked, sent off, or came on or off, from the moments (ADR-068). The moments, not the
 * events, because the page keeps only the latest events and every moment so far is loaded on open. A substitution's
 * `playerId` is the player coming on and its `relatedPlayerId` the player taken off; a goal's `relatedPlayerId` is the
 * assister. A second yellow is a yellow and then a red, as the moments record it.
 */
export function playerFacts(moments: Iterable<MomentView>): PlayerFacts {
  const facts: PlayerFacts = {};
  const add = (playerId: string | null | undefined, kind: PlayerFactKind, moment: MomentView) => {
    if (!playerId) {
      return;
    }

    (facts[playerId] ??= []).push({
      kind,
      momentId: moment.id,
      sequence: moment.sequence,
      clock: moment.clock,
    });
  };

  const ordered = [...moments].sort((a, b) => a.sequence - b.sequence);
  for (const moment of ordered) {
    switch (moment.kind) {
      case 'Goal':
        add(moment.playerId, 'goal', moment);
        add(moment.relatedPlayerId, 'assist', moment);
        break;
      case 'YellowCard':
        add(moment.playerId, 'yellow', moment);
        break;
      case 'RedCard':
        add(moment.playerId, 'red', moment);
        break;
      case 'Substitution':
        add(moment.playerId, 'subOn', moment);
        add(moment.relatedPlayerId, 'subOff', moment);
        break;
    }
  }

  return facts;
}

/** How many of one kind a player has, for a stats line such as "2 goals". */
export function countOf(facts: PlayerFact[] | undefined, kind: PlayerFactKind): number {
  return facts?.filter((f) => f.kind === kind).length ?? 0;
}

/** A leading match clock such as `92:02: ` that the server's template text starts with. */
const clockPrefix = /^\d{1,3}:\d{2}:\s*/;

/**
 * A moment's reason or commentary line without its leading clock. The server's template text starts with the raw
 * clock (`92:02: GOAL! ...`), which reads wrongly in added time, and the page shows the football minute beside it.
 */
export function stripClockPrefix(text: string): string {
  return text.replace(clockPrefix, '');
}

/** One goal of a scorer: its football minute and whether it was a penalty. */
export interface ScorerGoal {
  minute: string;
  penalty: boolean;
}

/** A player's goals for one side, for the scoreboard. */
export interface Scorer {
  /** The player's ID, or the moment's ID when the goal has none. */
  key: string;
  /** The full name, or `Goal` when the scorer isn't known. */
  name: string;
  goals: ScorerGoal[];
}

/** The last word of a name, which keeps the scoreboard compact. */
export function surname(name: string): string {
  const words = name.trim().split(/\s+/);
  return words[words.length - 1] ?? name;
}

/** `Ashworth 45+2', 90+3' (pen)`: the surname and each goal's minute, a penalty marked. */
export function scorerText(scorer: Scorer): string {
  const minutes = scorer.goals.map((g) => (g.penalty ? `${g.minute} (pen)` : g.minute)).join(', ');
  return `${surname(scorer.name)} ${minutes}`;
}

/** The same with the full name, for a screen reader. */
export function scorerLabel(scorer: Scorer): string {
  const minutes = scorer.goals.map((g) => (g.penalty ? `${g.minute} penalty` : g.minute)).join(', ');
  return `${scorer.name} ${minutes}`;
}

/**
 * Each side's scorers from the moments, in the order they first scored, with a player's goals grouped. A goal is
 * credited to the moment's own team, as the moments record it; the moments carry nothing that marks an own goal. A
 * penalty is read from the moment's reason ("from the penalty spot").
 */
export function scorersBySide(
  moments: Iterable<MomentView>,
  nameOf: (playerId: string) => string | undefined,
): { home: Scorer[]; away: Scorer[] } {
  const sides = { home: new Map<string, Scorer>(), away: new Map<string, Scorer>() };
  const ordered = [...moments].filter((m) => m.kind === 'Goal').sort((a, b) => a.sequence - b.sequence);
  for (const moment of ordered) {
    const map = moment.team === 'Away' ? sides.away : sides.home;
    const key = moment.playerId ?? moment.id;
    const scorer = map.get(key) ?? { key, name: (moment.playerId && nameOf(moment.playerId)) || 'Goal', goals: [] };
    scorer.goals.push({ minute: footballMinute(moment.clock), penalty: /penalty spot/i.test(moment.reason) });
    map.set(key, scorer);
  }
  return { home: [...sides.home.values()], away: [...sides.away.values()] };
}
