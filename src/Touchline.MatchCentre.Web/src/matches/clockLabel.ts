import { footballMinute } from '../match/playerFacts';
import type { MatchSummary } from './useMatches';

/**
 * The clock as a card shows it: `67:12` while a half is running, and `90+4'` once it has passed 45 or 90 minutes, so
 * stoppage time reads the way a scoreboard does rather than as `94:12`.
 */
export function clockLabel(clock: MatchSummary['clock']): string {
  const end = clock.period === 'FirstHalf' ? 45 : 90;
  return clock.minute >= end ? footballMinute(clock) : clock.display;
}
