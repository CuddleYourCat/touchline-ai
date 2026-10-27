import { useEffect, useId, useLayoutEffect, useRef, useState, type CSSProperties, type FormEvent, type KeyboardEvent } from 'react';
import type { Viewer } from '../viewer/viewer';
import { askQuestion, citedMoment, maxQuestionLength, momentAnchor, type AskResult, type Citation } from './askQuestion';
import './FanQuestions.css';

/** How many questions and answers stay in the chat, oldest first. */
export const maxExchanges = 5;

interface Exchange {
  id: number;
  question: string;
  result: AskResult | 'asking';
}

const failureText: Record<Exclude<AskResult['kind'], 'answered'>, string> = {
  unavailable: "The Analyst can't answer right now. The match carries on; try again in a moment.",
  busy: 'Lots of questions at once. Give it a few seconds and ask again.',
  invalid: `That question couldn't be asked. Keep it to ${maxQuestionLength} characters.`,
  notFound: "This match isn't available any more.",
};

/** Moves focus to a moment on the timeline, scrolling it into view. */
function jumpTo(momentId: string) {
  const target = document.getElementById(momentAnchor(momentId));
  if (!target) return;
  const still = typeof window.matchMedia === 'function' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  target.scrollIntoView?.({ behavior: still ? 'auto' : 'smooth', block: 'center' });
  target.focus({ preventScroll: true });
}

function CitationChip({
  citation,
  momentIds,
  onJump,
}: {
  citation: Citation;
  momentIds: ReadonlySet<string>;
  onJump: () => void;
}) {
  const moment = citedMoment(citation, momentIds);
  if (moment === undefined) return <span className="citation">{citation.label}</span>;
  return (
    <button
      type="button"
      className="citation citation-link"
      onClick={() => {
        onJump();
        jumpTo(moment);
      }}
      aria-label={`Jump to ${citation.label}`}
    >
      {citation.label}
    </button>
  );
}

function ExchangeItem({
  exchange,
  momentIds,
  onJump,
}: {
  exchange: Exchange;
  momentIds: ReadonlySet<string>;
  onJump: () => void;
}) {
  const { question, result } = exchange;
  return (
    <li className="chat-exchange">
      <p className="chat-bubble chat-bubble-fan">
        <span className="visually-hidden">You: </span>
        {question}
      </p>
      {result === 'asking' ? (
        <p className="chat-bubble chat-bubble-analyst muted" aria-busy="true">
          <span className="visually-hidden">Analyst: </span>Thinking<span className="dots" aria-hidden="true" />
        </p>
      ) : result.kind === 'answered' ? (
        <div className="chat-bubble chat-bubble-analyst" lang={result.answer.language}>
          <p>
            <span className="visually-hidden">Analyst: </span>
            {result.answer.text}
          </p>
          {result.answer.citations.length > 0 ? (
            <ul className="citations" aria-label="Based on">
              {result.answer.citations.map((c) => (
                <li key={`${c.kind}:${c.id}`}>
                  <CitationChip citation={c} momentIds={momentIds} onJump={onJump} />
                </li>
              ))}
            </ul>
          ) : (
            <p className="tag">No moment in the match backs this up.</p>
          )}
        </div>
      ) : (
        <p className="chat-bubble chat-bubble-analyst chat-failed">{failureText[result.kind]}</p>
      )}
    </li>
  );
}

/**
 * Fan Q&A (ADR-027) as a chat flyout pinned bottom-right (ADR-071): a pill opens a panel above it, with the fan's
 * questions on the right and the Analyst's answers on the left. The question is untrusted, and the answer is model
 * output, so both are rendered as React text only, never as HTML. A citation of a moment, or of the event that
 * triggered one, jumps to it on the timeline. A 503 or a dropped connection gives a friendly message, and the rest of
 * the match carries on. Focus goes into the box on open and back to the pill on close; Escape closes. A citation jump closes it too, so it never covers the moment it points at.
 */
const desktopQuery = '(min-width: 960px)';

/**
 * On desktop the open panel docks over the match page's Moments column: its box, or null when the column isn't there
 * (the CSS then docks it to the right edge) or on a phone, where the panel stays near full width.
 */
function useDock(open: boolean): { docked: boolean; style: CSSProperties | undefined } {
  const [dock, setDock] = useState<{ docked: boolean; style: CSSProperties | undefined }>({ docked: false, style: undefined });
  useLayoutEffect(() => {
    if (!open) return;
    const measure = () => {
      if (typeof window.matchMedia !== 'function' || !window.matchMedia(desktopQuery).matches) {
        setDock({ docked: false, style: undefined });
        return;
      }
      const rect = document.querySelector('.match-moments')?.getBoundingClientRect();
      setDock({
        docked: true,
        style: rect
          ? ({
              '--dock-top': `${rect.top}px`,
              '--dock-left': `${rect.left}px`,
              '--dock-width': `${rect.width}px`,
              '--dock-height': `${rect.height}px`,
            } as CSSProperties)
          : undefined,
      });
    };
    measure();
    window.addEventListener('resize', measure);
    return () => window.removeEventListener('resize', measure);
  }, [open]);
  return open ? dock : { docked: false, style: undefined };
}

export function FanQuestions({ matchId, viewer, momentIds }: { matchId: string; viewer: Viewer; momentIds: ReadonlySet<string> }) {
  const [open, setOpen] = useState(false);
  // An answer that lands while the panel is closed: a dot on the pill and a polite announcement.
  const [unread, setUnread] = useState(false);
  const openRef = useRef(false);
  const [draft, setDraft] = useState('');
  const [exchanges, setExchanges] = useState<Exchange[]>([]);
  const nextId = useRef(0);
  const inFlight = useRef<AbortController | null>(null);
  const launcherRef = useRef<HTMLButtonElement>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const chatRef = useRef<HTMLOListElement>(null);
  const dock = useDock(open);
  const counterId = useId();
  const panelId = `${counterId}-panel`;

  useEffect(() => () => inFlight.current?.abort(), []);

  useEffect(() => {
    if (open) inputRef.current?.focus();
  }, [open]);

  // Keep the newest message in view.
  useEffect(() => {
    const chat = chatRef.current;
    if (chat) chat.scrollTop = chat.scrollHeight;
  }, [exchanges, open]);

  useEffect(() => {
    openRef.current = open;
  }, [open]);

  const toggle = () => {
    setOpen((o) => !o);
    setUnread(false);
  };

  const close = () => {
    setOpen(false);
    launcherRef.current?.focus();
  };

  // The panel sits over the moments, so a citation closes it to show the moment it jumped to.
  const jumped = close;

  const question = draft.trim();
  const asking = exchanges.at(-1)?.result === 'asking';
  const canAsk = question.length > 0 && question.length <= maxQuestionLength && !asking;

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!canAsk) return;
    const id = ++nextId.current;
    const controller = new AbortController();
    inFlight.current = controller;
    setExchanges((list) => [...list, { id, question, result: 'asking' as const }].slice(-maxExchanges));
    setDraft('');
    inputRef.current?.focus(); // The send button disables while asking, so keep focus in the box.
    let result: AskResult;
    try {
      result = await askQuestion(matchId, question, viewer, controller.signal);
    } catch {
      return; // Aborted on unmount.
    }
    setExchanges((list) => list.map((x) => (x.id === id ? { ...x, result } : x)));
    if (!openRef.current) setUnread(true);
  }

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key === 'Escape' && open) {
      e.stopPropagation();
      close();
    }
  };

  return (
    <div className={dock.docked ? 'analyst analyst-docked' : 'analyst'} onKeyDown={onKeyDown}>
      <div className="visually-hidden" role="status" aria-label="Analyst announcements">
        {unread ? 'The Analyst has answered' : ''}
      </div>
      <section id={panelId} className="analyst-panel" style={dock.style} role="dialog" aria-label="Ask the Analyst" hidden={!open}>
        <header className="analyst-header">
          <h2>Ask the Analyst</h2>
          <button type="button" className="analyst-close" aria-label="Close" onClick={close}>
            <span aria-hidden="true">×</span>
          </button>
        </header>
        {exchanges.length === 0 && (
          <p className="analyst-empty muted">Ask about anything that has happened in this match so far.</p>
        )}
        <ol ref={chatRef} className="analyst-chat" aria-live="polite" aria-label="Answers">
          {exchanges.map((x) => (
            <ExchangeItem key={x.id} exchange={x} momentIds={momentIds} onJump={jumped} />
          ))}
        </ol>
        <form className="analyst-form" onSubmit={(e) => void submit(e)}>
          <label className="visually-hidden" htmlFor={`${counterId}-q`}>
            Your question about this match
          </label>
          <textarea
            ref={inputRef}
            id={`${counterId}-q`}
            className="analyst-input"
            rows={2}
            maxLength={maxQuestionLength}
            value={draft}
            placeholder="Why did the home side lose control after the break?"
            aria-describedby={counterId}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                e.currentTarget.form?.requestSubmit();
              }
            }}
          />
          <div className="analyst-actions">
            <span id={counterId} className="tag">
              {draft.length}/{maxQuestionLength}
            </span>
            <button type="submit" className="button" disabled={!canAsk}>
              {asking ? 'Asking…' : 'Send'}
            </button>
          </div>
        </form>
      </section>
      <button
        ref={launcherRef}
        type="button"
        className="analyst-launcher"
        aria-expanded={open}
        aria-controls={panelId}
        onClick={toggle}
      >
        <span aria-hidden="true" className="analyst-launcher-icon">
          ?
        </span>
        Ask the Analyst
        {unread && <span className="analyst-unread" aria-hidden="true" data-testid="analyst-unread" />}
      </button>
    </div>
  );
}
