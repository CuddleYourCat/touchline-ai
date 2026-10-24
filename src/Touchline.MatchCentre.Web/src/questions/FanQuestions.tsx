import { useEffect, useId, useRef, useState, type FormEvent } from 'react';
import type { Viewer } from '../viewer/viewer';
import { askQuestion, citedMoment, maxQuestionLength, momentAnchor, type AskResult, type Citation } from './askQuestion';

/** How many questions and answers stay on screen, newest first. */
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

function CitationChip({ citation, momentIds }: { citation: Citation; momentIds: ReadonlySet<string> }) {
  const moment = citedMoment(citation, momentIds);
  if (moment === undefined) return <span className="citation">{citation.label}</span>;
  return (
    <button type="button" className="citation citation-link" onClick={() => jumpTo(moment)} aria-label={`Jump to ${citation.label}`}>
      {citation.label}
    </button>
  );
}

function ExchangeItem({ exchange, momentIds }: { exchange: Exchange; momentIds: ReadonlySet<string> }) {
  const { question, result } = exchange;
  return (
    <li className="qa-exchange">
      <p className="qa-question">
        <span className="qa-who data">You</span> {question}
      </p>
      {result === 'asking' ? (
        <p className="qa-answer muted" aria-busy="true">
          <span className="qa-who data">Analyst</span> Thinking<span className="dots" aria-hidden="true" />
        </p>
      ) : result.kind === 'answered' ? (
        <div className="qa-answer" lang={result.answer.language}>
          <p>
            <span className="qa-who data">Analyst</span> {result.answer.text}
          </p>
          {result.answer.citations.length > 0 ? (
            <ul className="citations" aria-label="Based on">
              {result.answer.citations.map((c) => (
                <li key={`${c.kind}:${c.id}`}>
                  <CitationChip citation={c} momentIds={momentIds} />
                </li>
              ))}
            </ul>
          ) : (
            <p className="tag">No moment in the match backs this up.</p>
          )}
        </div>
      ) : (
        <p className="qa-answer qa-failed">
          {failureText[result.kind]}
        </p>
      )}
    </li>
  );
}

/**
 * Fan Q&A (ADR-027): a question box for the Analyst, with the answer and what it's based on. The question is
 * untrusted, and the answer is model output, so both are rendered as React text only, never as HTML. A citation of a
 * moment, or of the event that triggered one, jumps to it on the timeline. A 503 or a dropped connection gives a
 * friendly message, and the rest of the match carries on.
 */
export function FanQuestions({ matchId, viewer, momentIds }: { matchId: string; viewer: Viewer; momentIds: ReadonlySet<string> }) {
  const [draft, setDraft] = useState('');
  const [exchanges, setExchanges] = useState<Exchange[]>([]);
  const nextId = useRef(0);
  const inFlight = useRef<AbortController | null>(null);
  const counterId = useId();

  useEffect(() => () => inFlight.current?.abort(), []);

  const question = draft.trim();
  const asking = exchanges[0]?.result === 'asking';
  const canAsk = question.length > 0 && question.length <= maxQuestionLength && !asking;

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!canAsk) return;
    const id = ++nextId.current;
    const controller = new AbortController();
    inFlight.current = controller;
    setExchanges((list) => [{ id, question, result: 'asking' as const }, ...list].slice(0, maxExchanges));
    setDraft('');
    let result: AskResult;
    try {
      result = await askQuestion(matchId, question, viewer, controller.signal);
    } catch {
      return; // Aborted on unmount.
    }
    setExchanges((list) => list.map((x) => (x.id === id ? { ...x, result } : x)));
  }

  return (
    <section className="match-section qa" aria-label="Ask the Analyst">
      <h2>Ask the Analyst</h2>
      <form className="qa-form panel" onSubmit={(e) => void submit(e)}>
        <label className="viewer-label" htmlFor={`${counterId}-q`}>
          Your question about this match
        </label>
        <textarea
          id={`${counterId}-q`}
          className="qa-input"
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
        <div className="qa-actions">
          <span id={counterId} className="tag">
            {draft.length}/{maxQuestionLength}
          </span>
          <button type="submit" className="button" disabled={!canAsk}>
            {asking ? 'Asking…' : 'Ask'}
          </button>
        </div>
      </form>
      <ol className="qa-list" aria-live="polite" aria-label="Answers">
        {exchanges.map((x) => (
          <ExchangeItem key={x.id} exchange={x} momentIds={momentIds} />
        ))}
      </ol>
    </section>
  );
}
