import { api } from '../api/client';
import type { components } from '../api/schema';
import { viewerQuery, type Viewer } from '../viewer/viewer';

/** The Analyst's answer to a fan's question. */
export type FanAnswer = components['schemas']['FanAnswer'];
/** Something an answer is based on. */
export type Citation = components['schemas']['Citation'];

/** The longest question the server accepts (`QuestionRequest.MaxLength`), which bounds what a question costs in tokens. */
export const maxQuestionLength = 280;

/**
 * What came back. Each failure is a kind, never the server's text, so nothing internal reaches the page:
 * `unavailable` is a 503 or a network failure (the Analyst is down, the match carries on), `busy` is a 429,
 * `invalid` a 400 and `notFound` a 404.
 */
export type AskResult =
  | { kind: 'answered'; answer: FanAnswer }
  | { kind: 'unavailable' }
  | { kind: 'busy' }
  | { kind: 'invalid' }
  | { kind: 'notFound' };

/**
 * Posts a fan's question (`POST /api/matches/{matchId}/questions`). The question is untrusted: it travels only as the
 * JSON body, and the server keeps it out of every system prompt.
 */
export async function askQuestion(matchId: string, question: string, viewer: Viewer, signal?: AbortSignal): Promise<AskResult> {
  try {
    const { data, response } = await api.POST('/api/matches/{matchId}/questions', {
      params: { path: { matchId }, query: viewerQuery(viewer) },
      body: { question },
      signal,
    });
    if (data) return { kind: 'answered', answer: data };
    switch (response.status) {
      case 400:
        return { kind: 'invalid' };
      case 404:
        return { kind: 'notFound' };
      case 429:
        return { kind: 'busy' };
      default:
        return { kind: 'unavailable' };
    }
  } catch (error) {
    if (signal?.aborted) throw error;
    return { kind: 'unavailable' };
  }
}

/** The safe DOM ID of a moment on the timeline, so an answer's citation can jump to it. */
export function momentAnchor(momentId: string): string {
  return `moment-${momentId.replace(/[^A-Za-z0-9_-]/g, '-')}`;
}

/**
 * The timeline moment a citation points at, if it's on the timeline: a moment by its ID, or an event (`event:m1:412`)
 * through the moment it triggered (`m1:412`). Possessions and the match summary have no moment.
 */
export function citedMoment(citation: Citation, momentIds: ReadonlySet<string>): string | undefined {
  const id =
    citation.kind === 'Moment' ? citation.id : citation.kind === 'Event' && citation.id.startsWith('event:') ? citation.id.slice(6) : undefined;
  return id !== undefined && momentIds.has(id) ? id : undefined;
}
