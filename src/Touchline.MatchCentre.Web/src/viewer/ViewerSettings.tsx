import { useId } from 'react';
import { usePitchColour } from '../theme/pitchColour';
import { languages, viewingModes, type Viewer } from './viewer';

/** A club the viewer can pick as their favourite. */
export interface ClubOption {
  id: string;
  name: string;
}

interface ViewerSettingsProps {
  viewer: Viewer;
  onChange: (next: Viewer) => void;
  /** The clubs in this match, offered as favourites. */
  clubs: readonly ClubOption[];
  /** Offers the grey-pitch option. Set only where there's a pitch to colour (the match page). */
  showPitchOption?: boolean;
}

/**
 * The viewing-mode picker (ADR-027): Casual or Analyst, the language, and a favourite club from this match. The choice
 * is the viewer's own and is saved in the browser; the commentary, answers and recap follow it. The grey-pitch option
 * is an accessibility setting for the pitch's colours; it's kept beside the theme, not in the {@link Viewer}, because
 * it never goes to the service.
 */
export function ViewerSettings({ viewer, onChange, clubs, showPitchOption = false }: ViewerSettingsProps) {
  const id = useId();
  const [pitch, setPitch] = usePitchColour();
  const knownClub = !viewer.team || clubs.some((c) => c.id === viewer.team);

  const set = (patch: Partial<Viewer>) => {
    const next: Viewer = { ...viewer, ...patch };
    if (!next.team) delete next.team;
    onChange(next);
  };

  return (
    <section className="panel viewer-settings" aria-label="Your view">
      <fieldset className="viewer-modes">
        <legend className="viewer-label">View</legend>
        <div className="segmented">
          {viewingModes.map(({ mode, label, hint }) => (
            <label key={mode} className={viewer.mode === mode ? 'segment segment-on' : 'segment'} title={hint}>
              <input
                type="radio"
                name={`${id}-mode`}
                value={mode}
                checked={viewer.mode === mode}
                onChange={() => set({ mode })}
              />
              {label}
            </label>
          ))}
        </div>
      </fieldset>
      <label className="viewer-field">
        <span className="viewer-label">Language</span>
        <select className="select" value={viewer.language} onChange={(e) => set({ language: e.target.value })}>
          {languages.map((l) => (
            <option key={l.tag} value={l.tag} lang={l.tag}>
              {l.label}
            </option>
          ))}
        </select>
      </label>
      <label className="viewer-field">
        <span className="viewer-label">Favourite club</span>
        <select className="select" value={viewer.team ?? ''} onChange={(e) => set({ team: e.target.value || undefined })}>
          <option value="">No favourite</option>
          {clubs.map((c) => (
            <option key={c.id} value={c.id}>
              {c.name}
            </option>
          ))}
          {!knownClub && viewer.team && <option value={viewer.team}>Your club (not in this match)</option>}
        </select>
      </label>
      {showPitchOption && (
        <fieldset className="viewer-access">
          <legend className="viewer-label">
            <span aria-hidden="true" className="viewer-access-icon">
              &#9855;
            </span>
            Accessibility
          </legend>
          <label className="viewer-check">
            <input
              type="checkbox"
              aria-describedby={`${id}-pitch-hint`}
              checked={pitch === 'grey'}
              onChange={(e) => setPitch(e.target.checked ? 'grey' : 'green')}
            />
            Grey pitch
          </label>
          <p id={`${id}-pitch-hint`} className="viewer-hint">
            Makes the players easier to pick out from the grass, including for colour-blind viewers.
          </p>
        </fieldset>
      )}
    </section>
  );
}
