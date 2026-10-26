import { describe, expect, it } from 'vitest';
import { clockLabel } from './clockLabel';

type Clock = Parameters<typeof clockLabel>[0];

const clock = (period: Clock['period'], minute: number, second: number): Clock => ({
  period,
  minute,
  second,
  display: `${String(minute).padStart(2, '0')}:${String(second).padStart(2, '0')}`,
});

describe('clockLabel', () => {
  it('shows the running clock inside a half', () => {
    expect(clockLabel(clock('SecondHalf', 67, 12))).toBe('67:12');
    expect(clockLabel(clock('FirstHalf', 44, 59))).toBe('44:59');
  });

  it('shows first-half stoppage time as 45+N', () => {
    expect(clockLabel(clock('FirstHalf', 45, 0))).toBe("45+1'");
    expect(clockLabel(clock('FirstHalf', 47, 30))).toBe("45+3'");
  });

  it('shows second-half stoppage time as 90+N', () => {
    expect(clockLabel(clock('SecondHalf', 90, 5))).toBe("90+1'");
    expect(clockLabel(clock('SecondHalf', 93, 40))).toBe("90+4'");
  });
});
