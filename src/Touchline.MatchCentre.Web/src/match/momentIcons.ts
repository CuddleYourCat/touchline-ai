import type { PlayerFactKind } from './playerFacts';

/** Every icon the match panels draw (ADR-068). The player-fact kinds, plus the timeline's other moments. */
export type MomentIconKind = PlayerFactKind | 'penalty' | 'chance' | 'clearance' | 'sub';

/** The icon for a moment kind on the timeline, or null for a moment the timeline leaves out (corners, restarts). */
export function iconForMoment(kind: string): MomentIconKind | null {
  switch (kind) {
    case 'Goal':
      return 'goal';
    case 'YellowCard':
      return 'yellow';
    case 'RedCard':
      return 'red';
    case 'Substitution':
      return 'sub';
    case 'PenaltyAwarded':
    case 'PenaltySaved':
    case 'PenaltyMissed':
      return 'penalty';
    case 'BigChance':
      return 'chance';
    case 'ClearedOffLine':
      return 'clearance';
    default:
      return null;
  }
}

/** The plain-words name of an icon, for its accessible label. */
export const momentIconLabel: Record<MomentIconKind, string> = {
  goal: 'Goal',
  assist: 'Assist',
  yellow: 'Yellow card',
  red: 'Red card',
  subOn: 'Came on',
  subOff: 'Went off',
  sub: 'Substitution',
  penalty: 'Penalty',
  chance: 'Big chance',
  clearance: 'Cleared off the line',
};
