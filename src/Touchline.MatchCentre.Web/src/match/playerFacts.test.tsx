import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { MomentView } from '../live/matchStreamReducer';
import { MomentIcon } from './MomentIcon';
import { iconForMoment } from './momentIcons';
import { countOf, footballMinute, playerFacts } from './playerFacts';

function moment(
  sequence: number,
  kind: string,
  playerId: string | null,
  relatedPlayerId: string | null = null,
  minute = 10,
): MomentView {
  return {
    id: `m:${sequence}`,
    tier: 'Key',
    kind,
    sequence,
    clock: {
      period: minute < 45 ? 'FirstHalf' : 'SecondHalf',
      minute,
      second: 0,
      display: `${minute}:00`,
    },
    team: 'Home',
    playerId,
    reason: kind,
    commentary: null,
    relatedPlayerId,
  } as MomentView;
}

describe('playerFacts', () => {
  it('credits the scorer and the assister, in match order', () => {
    const facts = playerFacts([moment(20, 'Goal', 'h9', 'h10', 30), moment(5, 'Goal', 'h9', null, 3)]);

    expect(facts.h9?.map((f) => [f.kind, f.sequence])).toEqual([
      ['goal', 5],
      ['goal', 20],
    ]);
    expect(facts.h10).toEqual([
      {
        kind: 'assist',
        momentId: 'm:20',
        sequence: 20,
        clock: expect.objectContaining({ minute: 30 }),
      },
    ]);
    expect(countOf(facts.h9, 'goal')).toBe(2);
  });

  it('records a substitution for the player coming on and the one going off', () => {
    const facts = playerFacts([moment(40, 'Substitution', 'h14', 'h7', 66)]);

    expect(facts.h14?.map((f) => f.kind)).toEqual(['subOn']);
    expect(facts.h7?.map((f) => f.kind)).toEqual(['subOff']);
  });

  it('keeps cards and ignores moments that are about no one in particular', () => {
    const facts = playerFacts([
      moment(1, 'YellowCard', 'a4'),
      moment(2, 'RedCard', 'a4'),
      moment(3, 'Corner', null),
      moment(4, 'BigChance', 'h9'),
    ]);

    expect(facts.a4?.map((f) => f.kind)).toEqual(['yellow', 'red']);
    expect(facts.h9).toBeUndefined();
    expect(countOf(undefined, 'goal')).toBe(0);
  });
});

describe('footballMinute', () => {
  it.each([
    ['FirstHalf', 0, "1'"],
    ['FirstHalf', 44, "45'"],
    ['FirstHalf', 46, "45+2'"],
    ['SecondHalf', 45, "46'"],
    ['SecondHalf', 67, "68'"],
    ['SecondHalf', 92, "90+3'"],
  ] as const)('%s at minute %i reads %s', (period, minute, expected) => {
    expect(footballMinute({ period, minute, second: 12, display: '' })).toBe(expected);
  });
});

describe('MomentIcon', () => {
  it('is decorative without a label and an image with one', () => {
    const { container } = render(<MomentIcon kind="goal" />);
    expect(container.querySelector('svg')).toHaveAttribute('aria-hidden', 'true');

    render(<MomentIcon kind="red" label="Red card, 70'" />);
    expect(screen.getByRole('img', { name: "Red card, 70'" })).toHaveAttribute('data-kind', 'red');
  });

  it('maps the timeline moments and leaves restarts out', () => {
    expect(iconForMoment('PenaltySaved')).toBe('penalty');
    expect(iconForMoment('Substitution')).toBe('sub');
    expect(iconForMoment('ClearedOffLine')).toBe('clearance');
    expect(iconForMoment('ThrowIn')).toBeNull();
  });
});
