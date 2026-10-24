import { useId } from 'react';
import type { CrestSpec, TeamBuilderOptions } from './teamDraft';

/** Outline paths in a 100 x 100 box, one per crest shape. */
const outlines: Readonly<Record<CrestSpec['shape'], string>> = {
  Shield: 'M50 4 L92 16 L88 58 Q82 84 50 96 Q18 84 12 58 L8 16 Z',
  Round: 'M50 4 A46 46 0 1 1 49.9 4 Z',
  Diamond: 'M50 4 L96 50 L50 96 L4 50 Z',
  Banner: 'M10 4 H90 V92 L50 76 L10 92 Z',
  Hexagon: 'M50 4 L90 27 V73 L50 96 L10 73 V27 Z',
};

/**
 * A local swatch of the crest: its outline, colours and initials. There's no preview endpoint, so this is an
 * approximation; the service renders the real crest (`crest.svg`) once the team is saved. The emblem is named, not drawn.
 */
export function CrestPreview({ crest, size = 96 }: { crest: CrestSpec; size?: number }) {
  return (
    <figure className="crest-preview">
      <svg viewBox="0 0 100 100" width={size} height={size} role="img" aria-label={`Crest preview: ${crest.shape.toLowerCase()}, ${crest.emblem.toLowerCase()} emblem`}>
        <path d={outlines[crest.shape]} fill={crest.primary} stroke={crest.secondary} strokeWidth={6} strokeLinejoin="round" />
        {crest.initials && (
          <text x="50" y="60" textAnchor="middle" fontSize="28" fontWeight="700" fill={crest.secondary} fontFamily="var(--font-display)">
            {crest.initials}
          </text>
        )}
      </svg>
      <figcaption className="tag">{crest.emblem === 'None' ? 'No emblem' : `${crest.emblem} emblem`} &middot; preview</figcaption>
    </figure>
  );
}

interface CrestPickerProps {
  crest: CrestSpec;
  options: TeamBuilderOptions;
  onChange: (next: CrestSpec) => void;
  onNameBlur: () => void;
  initialsError?: string;
}

/** The crest builder (ADR-031): a shape, an emblem from the fixed set, up to three initials and two colours. */
export function CrestPicker({ crest, options, onChange, onNameBlur, initialsError }: CrestPickerProps) {
  const id = useId();
  const set = (patch: Partial<CrestSpec>) => onChange({ ...crest, ...patch });

  return (
    <fieldset className="builder-group crest-picker">
      <legend className="builder-legend">Crest</legend>
      <CrestPreview crest={crest} />
      <div className="builder-fields">
        <label className="builder-field">
          <span className="viewer-label">Shape</span>
          <select className="select" value={crest.shape} onChange={(e) => set({ shape: e.target.value as CrestSpec['shape'] })}>
            {options.crestShapes.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </select>
        </label>
        <label className="builder-field">
          <span className="viewer-label">Emblem</span>
          <select className="select" value={crest.emblem} onChange={(e) => set({ emblem: e.target.value as CrestSpec['emblem'] })}>
            {options.crestEmblems.map((e) => (
              <option key={e} value={e}>
                {e === 'None' ? 'No emblem' : e}
              </option>
            ))}
          </select>
        </label>
        <label className="builder-field">
          <span className="viewer-label">Initials</span>
          <input
            className="input data"
            value={crest.initials}
            maxLength={3}
            autoComplete="off"
            aria-invalid={initialsError ? true : undefined}
            aria-describedby={initialsError ? `${id}-initials` : undefined}
            onChange={(e) => set({ initials: e.target.value.toUpperCase().replace(/[^A-Z]/g, '') })}
            onBlur={onNameBlur}
          />
          {initialsError && (
            <span id={`${id}-initials`} className="field-error">
              {initialsError}
            </span>
          )}
        </label>
        <label className="builder-field builder-colour">
          <span className="viewer-label">Crest main colour</span>
          <input type="color" value={crest.primary} onChange={(e) => set({ primary: e.target.value })} />
        </label>
        <label className="builder-field builder-colour">
          <span className="viewer-label">Crest trim colour</span>
          <input type="color" value={crest.secondary} onChange={(e) => set({ secondary: e.target.value })} />
        </label>
      </div>
    </fieldset>
  );
}
