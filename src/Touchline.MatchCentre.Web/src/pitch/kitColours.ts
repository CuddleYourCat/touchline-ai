/** A club's kit: the shirt colour and the trim, as `#rrggbb`. */
export interface Kit {
  primary: string;
  secondary: string;
}

/** What a player token is drawn with: the shirt colour, the rim, and the number's colour. */
export interface TokenColours {
  fill: string;
  rim: string;
  ink: string;
}

const hexColour = /^#[0-9a-f]{6}$/i;
const white = '#ffffff';
const ink = '#080d1a';
// WCAG 1.4.11: graphical objects need 3:1 against what they sit on.
const minRimContrast = 3;
// Both pitch colours and their mowing stripes, so a rim is judged against the grass in either setting.
// Keep in step with the --pitch and --pitch-stripe tokens in src/styles.css (default, light theme and data-pitch='grey').
const pitchColours = ['#145f33', '#1f7a44', '#207340', '#2e8a50', '#4a515c', '#5c6470'];

/** A kit from whatever the service sent, or null when either colour isn't a plain `#rrggbb` (the tokens then keep the house red and blue). */
export function parseKit(colours: unknown): Kit | null {
  if (typeof colours !== 'object' || colours === null) return null;
  const { primary, secondary } = colours as Record<string, unknown>;
  return typeof primary === 'string' && typeof secondary === 'string' && hexColour.test(primary) && hexColour.test(secondary)
    ? { primary, secondary }
    : null;
}

function luminance(hex: string): number {
  const channel = (offset: number) => {
    const c = parseInt(hex.slice(offset, offset + 2), 16) / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(1) + 0.7152 * channel(3) + 0.0722 * channel(5);
}

/** The WCAG contrast ratio between two `#rrggbb` colours, from 1 to 21. */
export function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number];
  return (hi + 0.05) / (lo + 0.05);
}

/**
 * The colours for one side's tokens: the shirt colour as the fill and the trim as the rim, as a fan would see the kit.
 * The number is white or near-black, whichever reads better on the shirt. A trim that would vanish into the grass, or
 * into the shirt, is swapped for white, so a token never loses its outline.
 */
export function tokenColours(kit: Kit): TokenColours {
  const rimShows =
    Math.min(...pitchColours.map((p) => contrast(kit.secondary, p))) >= minRimContrast && contrast(kit.secondary, kit.primary) >= 1.5;
  return {
    fill: kit.primary,
    rim: rimShows ? kit.secondary : white,
    ink: contrast(white, kit.primary) >= contrast(ink, kit.primary) ? white : ink,
  };
}
