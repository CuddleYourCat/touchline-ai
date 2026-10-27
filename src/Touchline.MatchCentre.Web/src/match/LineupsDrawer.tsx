import { useEffect, useRef } from 'react';
import type { MatchDetail, MomentView } from '../live/matchStreamReducer';
import { useReducedMotion } from '../pitch/useReducedMotion';
import { LineupsCard } from './LineupsCard';
import './LineupsDrawer.css';

export interface LineupsDrawerProps {
  /** The id the opening button points at with `aria-controls`. */
  id: string;
  detail: MatchDetail;
  /** Every moment so far, any order. */
  moments: MomentView[];
  selectedPlayerId: string | null;
  onSelectPlayer: (playerId: string) => void;
  onClose: () => void;
  /** True while the player card is open on top: Escape then belongs to it, not to this drawer. */
  suspended: boolean;
}

/**
 * Both line-ups in a drawer that slides in from the left (ADR-071). It isn't modal: the player card can open on top
 * from the right, and the pitch stays in view. Focus moves to the close button on open and goes back to whatever
 * opened the drawer on close. Escape closes it, unless the player card is open, which closes first.
 */
export function LineupsDrawer({ id, detail, moments, selectedPlayerId, onSelectPlayer, onClose, suspended }: LineupsDrawerProps) {
  const closeRef = useRef<HTMLButtonElement>(null);
  const onCloseRef = useRef(onClose);
  const suspendedRef = useRef(suspended);
  const reduced = useReducedMotion();

  useEffect(() => {
    onCloseRef.current = onClose;
    suspendedRef.current = suspended;
  });

  useEffect(() => {
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    closeRef.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && !e.defaultPrevented && !suspendedRef.current) onCloseRef.current();
    };
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('keydown', onKey);
      opener?.focus();
    };
  }, []);

  return (
    <div
      id={id}
      className={reduced ? 'drawer drawer-left drawer-still' : 'drawer drawer-left'}
      role="dialog"
      aria-modal="false"
      aria-labelledby={`${id}-title`}
    >
      <header className="drawer-header">
        <h2 id={`${id}-title`}>Line-ups</h2>
        <button ref={closeRef} type="button" className="drawer-close" aria-label="Close line-ups" onClick={onClose}>
          <span aria-hidden="true">×</span>
        </button>
      </header>
      <div className="drawer-body">
        <LineupsCard detail={detail} moments={moments} onSelectPlayer={onSelectPlayer} selectedPlayerId={selectedPlayerId} />
      </div>
    </div>
  );
}
