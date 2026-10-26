import type { MomentIconKind } from './momentIcons';

export interface MomentIconProps {
  kind: MomentIconKind;
  /** Size in CSS pixels. Defaults to 16. */
  size?: number;
  /** When set, the icon is labelled for screen readers; otherwise it's decorative and hidden from them. */
  label?: string;
  className?: string;
}

/**
 * A small inline SVG icon for a moment. Cards and substitution arrows use `--card-yellow`, `--card-red` and `--sub-on` (with fallbacks), so a theme can
 * restyle them; everything else draws in `currentColor`. Shared by the line-ups card, the timeline and the player flyout.
 */
export function MomentIcon({ kind, size = 16, label, className }: MomentIconProps) {
  const a11y = label ? { role: 'img', 'aria-label': label } : { 'aria-hidden': true };
  return (
    <svg
      className={['moment-icon', className].filter(Boolean).join(' ')}
      data-kind={kind}
      width={size}
      height={size}
      viewBox="0 0 16 16"
      focusable="false"
      {...a11y}
    >
      {glyph(kind)}
    </svg>
  );
}

function glyph(kind: MomentIconKind) {
  switch (kind) {
    case 'goal':
      return (
        <>
          <circle cx="8" cy="8" r="6.5" fill="none" stroke="currentColor" strokeWidth="1.5" />
          <path d="M8 4.5 11 6.7 9.9 10.2H6.1L5 6.7Z" fill="currentColor" />
        </>
      );
    case 'assist':
      return (
        <path
          d="M2 11.5h8.5M7.5 8.5l3 3-3 3M10 4.5a2 2 0 1 1 4 0 2 2 0 0 1-4 0"
          fill="none"
          stroke="currentColor"
          strokeWidth="1.5"
          strokeLinecap="round"
          strokeLinejoin="round"
        />
      );
    case 'yellow':
      return <rect x="4" y="2" width="8" height="12" rx="1.2" fill="var(--card-yellow, #f5c518)" stroke="#00000033" />;
    case 'red':
      return <rect x="4" y="2" width="8" height="12" rx="1.2" fill="var(--card-red, #d7263d)" stroke="#00000033" />;
    case 'subOn':
      return (
        <path
          d="M8 13V3M4 7l4-4 4 4"
          fill="none"
          stroke="var(--sub-on, #2e9e5b)"
          strokeWidth="2"
          strokeLinecap="round"
          strokeLinejoin="round"
        />
      );
    case 'subOff':
      return (
        <path
          d="M8 3v10M4 9l4 4 4-4"
          fill="none"
          stroke="var(--card-red, #d7263d)"
          strokeWidth="2"
          strokeLinecap="round"
          strokeLinejoin="round"
        />
      );
    case 'sub':
      return (
        <>
          <path
            d="M5 13V4M2.5 6.5 5 4l2.5 2.5"
            fill="none"
            stroke="var(--sub-on, #2e9e5b)"
            strokeWidth="1.75"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
          <path
            d="M11 3v9M8.5 9.5 11 12l2.5-2.5"
            fill="none"
            stroke="var(--card-red, #d7263d)"
            strokeWidth="1.75"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        </>
      );
    case 'penalty':
      return (
        <>
          <path d="M1.5 4.5h13v7" fill="none" stroke="currentColor" strokeWidth="1.25" />
          <path d="M1.5 4.5v7" fill="none" stroke="currentColor" strokeWidth="1.25" />
          <circle cx="8" cy="11" r="1.75" fill="currentColor" />
        </>
      );
    case 'chance':
    case 'clearance':
      return (
        <>
          <circle cx="8" cy="8" r="6" fill="none" stroke="currentColor" strokeWidth="1.25" />
          <circle cx="8" cy="8" r="3" fill="none" stroke="currentColor" strokeWidth="1.25" />
          <circle cx="8" cy="8" r="1" fill="currentColor" />
        </>
      );
  }
}
