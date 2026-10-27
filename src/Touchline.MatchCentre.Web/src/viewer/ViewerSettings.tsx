import { useEffect, useId, useRef, useState, type FocusEvent } from 'react';
import { usePitchColour } from '../theme/pitchColour';
import { languages, viewingModes, type Viewer } from './viewer';
import './ViewerSettings.css';

interface ViewerSettingsProps {
  viewer: Viewer;
  onChange: (next: Viewer) => void;
  /** Offers the grey-pitch option. Set only where there's a pitch to colour (the match page). */
  showPitchOption?: boolean;
}

/** A cog, drawn inline so it takes the button's colour. */
function Gear() {
  return (
    <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true" focusable="false">
      <path
        fill="none"
        stroke="currentColor"
        strokeWidth="1.8"
        strokeLinecap="round"
        strokeLinejoin="round"
        d="M12 15.2a3.2 3.2 0 1 0 0-6.4 3.2 3.2 0 0 0 0 6.4Zm7.4-2.2a7.5 7.5 0 0 0 0-2l2-1.5-2-3.5-2.4 1a7.4 7.4 0 0 0-1.7-1L15 3.4h-4l-.3 2.6a7.4 7.4 0 0 0-1.7 1l-2.4-1-2 3.5 2 1.5a7.5 7.5 0 0 0 0 2l-2 1.5 2 3.5 2.4-1a7.4 7.4 0 0 0 1.7 1l.3 2.6h4l.3-2.6a7.4 7.4 0 0 0 1.7-1l2.4 1 2-3.5-2-1.5Z"
      />
    </svg>
  );
}

/**
 * The viewing-mode settings (ADR-027), behind a gear button (ADR-071): Casual or Analyst, the language, and, on the
 * match page, the grey-pitch option. The choice is the viewer's own and is saved in the browser; the commentary,
 * answers and recap follow it. The popover closes on Escape (focus goes back to the gear), on a click outside it, and
 * when focus leaves it. The grey-pitch option is an accessibility setting for the pitch's colours; it's kept beside
 * the theme, not in the {@link Viewer}, because it never goes to the service.
 */
export function ViewerSettings({ viewer, onChange, showPitchOption = false }: ViewerSettingsProps) {
  const id = useId();
  const [pitch, setPitch] = usePitchColour();
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const gearRef = useRef<HTMLButtonElement>(null);
  const popoverId = `${id}-popover`;

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      // Taken here, so a drawer or card under the popover keeps its own Escape for next time.
      e.preventDefault();
      setOpen(false);
      gearRef.current?.focus();
    };
    const onPointerDown = (e: MouseEvent) => {
      if (e.target instanceof Node && !rootRef.current?.contains(e.target)) setOpen(false);
    };
    // Capture phase, so the popover sees Escape before anything that opened earlier.
    document.addEventListener('keydown', onKey, true);
    document.addEventListener('mousedown', onPointerDown);
    return () => {
      document.removeEventListener('keydown', onKey, true);
      document.removeEventListener('mousedown', onPointerDown);
    };
  }, [open]);

  // Tabbing out of the popover closes it, so it never hangs open over the page.
  const onBlur = (e: FocusEvent<HTMLDivElement>) => {
    if (open && e.relatedTarget instanceof Node && !e.currentTarget.contains(e.relatedTarget)) setOpen(false);
  };

  const set = (patch: Partial<Viewer>) => onChange({ ...viewer, ...patch });

  return (
    <div className="settings" ref={rootRef} onBlur={onBlur}>
      <button
        ref={gearRef}
        type="button"
        className="icon-button"
        aria-label="Settings"
        aria-expanded={open}
        aria-controls={popoverId}
        onClick={() => setOpen((o) => !o)}
      >
        <Gear />
      </button>
      <div id={popoverId} className="settings-popover" role="dialog" aria-label="Your view" hidden={!open}>
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
      </div>
    </div>
  );
}
