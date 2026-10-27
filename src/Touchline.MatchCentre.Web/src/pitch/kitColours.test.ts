import { describe, expect, it } from 'vitest';
import { contrast, parseKit, tokenColours } from './kitColours';

describe('kit colours', () => {
  it('reads a kit of two #rrggbb colours, and nothing else', () => {
    expect(parseKit({ primary: '#0b1f4f', secondary: '#d4af37' })).toEqual({ primary: '#0b1f4f', secondary: '#d4af37' });
    expect(parseKit({ primary: 'navy', secondary: '#d4af37' })).toBeNull();
    expect(parseKit({ primary: '#0b1f4f' })).toBeNull();
    expect(parseKit(undefined)).toBeNull();
    expect(parseKit([])).toBeNull();
  });

  it('measures contrast from 1 (the same colour) to 21 (black on white)', () => {
    expect(contrast('#336699', '#336699')).toBeCloseTo(1);
    expect(contrast('#000000', '#ffffff')).toBeCloseTo(21);
  });

  it('draws navy and gold as a navy token with a gold rim and a white number', () => {
    expect(tokenColours({ primary: '#0b1f4f', secondary: '#ffe066' })).toEqual({ fill: '#0b1f4f', rim: '#ffe066', ink: '#ffffff' });
  });

  it('draws red and white as a red token with a white rim', () => {
    expect(tokenColours({ primary: '#d62828', secondary: '#ffffff' }).rim).toBe('#ffffff');
  });

  it('puts a dark number on a light shirt', () => {
    expect(tokenColours({ primary: '#ffe066', secondary: '#0b1f4f' }).ink).toBe('#080d1a');
  });

  it('swaps a trim that clears 1.8:1 but not 3:1 against the grass for white', () => {
    expect(tokenColours({ primary: '#0b1f4f', secondary: '#d4af37' }).rim).toBe('#ffffff');
  });

  it('swaps a trim that would vanish into the grass or the shirt for white', () => {
    expect(tokenColours({ primary: '#d62828', secondary: '#207340' }).rim).toBe('#ffffff');
    expect(tokenColours({ primary: '#d62828', secondary: '#d92a2a' }).rim).toBe('#ffffff');
  });
});
