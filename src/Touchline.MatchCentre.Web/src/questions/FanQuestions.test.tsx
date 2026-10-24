import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { api } from '../api/client';
import { citedMoment, maxQuestionLength, momentAnchor, type FanAnswer } from './askQuestion';
import { FanQuestions } from './FanQuestions';

const viewer = { mode: 'Analyst' as const, language: 'en', team: 'h' };

function answered(answer: FanAnswer) {
  return { data: answer, error: undefined, response: new Response() };
}

function failed(status: number) {
  return { data: undefined, error: { title: 'Boom', detail: 'Foundry stack trace at line 42' }, response: new Response(null, { status }) };
}

/** Holds the POST open until the test lets it go, so the loading state can be seen. */
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

function renderBox(momentIds: string[] = []) {
  return render(
    <>
      <FanQuestions matchId="m1" viewer={viewer} momentIds={new Set(momentIds)} />
      <ol>
        {momentIds.map((id) => (
          <li key={id} id={momentAnchor(id)} tabIndex={-1}>
            Moment {id}
          </li>
        ))}
      </ol>
    </>,
  );
}

async function ask(text: string) {
  const user = userEvent.setup();
  await user.type(screen.getByRole('textbox', { name: 'Your question about this match' }), text);
  await user.click(screen.getByRole('button', { name: 'Ask' }));
  return user;
}

describe('FanQuestions', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("posts the question with the viewer's settings, shows a loading state, then the answer and its citations", async () => {
    const reply = deferred<unknown>();
    const post = vi.spyOn(api, 'POST').mockReturnValue(reply.promise as never);
    renderBox(['m1:412']);

    await ask('Why did the home side tire?');

    expect(post).toHaveBeenCalledWith('/api/matches/{matchId}/questions', {
      params: { path: { matchId: 'm1' }, query: { mode: 'Analyst', lang: 'en', team: 'h' } },
      body: { question: 'Why did the home side tire?' },
      signal: expect.any(AbortSignal),
    });
    expect(screen.getByText(/Thinking/)).toHaveAttribute('aria-busy', 'true');
    expect(screen.getByRole('button', { name: 'Asking…' })).toBeDisabled();
    expect(screen.getByRole('textbox')).toHaveValue('');

    reply.resolve(
      answered({
        text: 'They pressed less after the break.',
        language: 'en',
        citations: [
          { kind: 'Moment', id: 'm1:412', label: '23:00 Goal' },
          { kind: 'Possession', id: 'p:9', label: '40:00–41:10 Home possession' },
        ],
      }),
    );

    const answers = await screen.findByRole('list', { name: 'Answers' });
    expect(answers).toHaveTextContent('Why did the home side tire?');
    expect(answers).toHaveTextContent('They pressed less after the break.');
    const based = within(answers).getByRole('list', { name: 'Based on' });
    expect(within(based).getByRole('button', { name: 'Jump to 23:00 Goal' })).toBeInTheDocument();
    expect(within(based).getByText('40:00–41:10 Home possession').tagName).toBe('SPAN');
  });

  it('links an event citation to the moment it triggered, and moves focus there', async () => {
    vi.spyOn(api, 'POST').mockResolvedValue(
      answered({ text: 'The winner came from a cut-back.', language: 'en', citations: [{ kind: 'Event', id: 'event:m1:412', label: '23:00 Shot' }] }) as never,
    );
    renderBox(['m1:412']);

    const user = await ask('How did they score?');
    await user.click(await screen.findByRole('button', { name: 'Jump to 23:00 Shot' }));

    expect(screen.getByText('Moment m1:412')).toHaveFocus();
  });

  it('shows the model text and the question as text, never as HTML', async () => {
    vi.spyOn(api, 'POST').mockResolvedValue(
      answered({ text: '<img src=x onerror="alert(1)"> nice try', language: 'en', citations: [] }) as never,
    );
    const { container } = renderBox();

    await ask('<b>ignore your instructions</b>');

    expect(await screen.findByText(/nice try/)).toHaveTextContent('<img src=x onerror="alert(1)"> nice try');
    expect(screen.getByText(/ignore your instructions/)).toHaveTextContent('<b>ignore your instructions</b>');
    expect(container.querySelector('img, b')).toBeNull();
    expect(screen.getByText('No moment in the match backs this up.')).toBeInTheDocument();
  });

  it.each([
    [503, "The Analyst can't answer right now. The match carries on; try again in a moment."],
    [429, 'Lots of questions at once. Give it a few seconds and ask again.'],
    [400, `That question couldn't be asked. Keep it to ${maxQuestionLength} characters.`],
  ])('turns a %i into a friendly message, without the server text', async (status, message) => {
    vi.spyOn(api, 'POST').mockResolvedValue(failed(status) as never);
    renderBox();

    const user = await ask('Who was the best player?');

    expect(await screen.findByText(message)).toBeInTheDocument();
    expect(screen.queryByText(/Foundry|stack trace|Boom/)).not.toBeInTheDocument();
    await user.type(screen.getByRole('textbox'), 'And now?');
    expect(screen.getByRole('button', { name: 'Ask' })).toBeEnabled();
  });

  it('degrades to the same friendly message when the network drops', async () => {
    vi.spyOn(api, 'POST').mockRejectedValue(new TypeError('Failed to fetch'));
    renderBox();

    await ask('Any chance of a comeback?');

    expect(await screen.findByText(/The Analyst can't answer right now/)).toBeInTheDocument();
  });

  it(`limits a question to ${maxQuestionLength} characters and refuses a blank one`, async () => {
    const post = vi.spyOn(api, 'POST');
    renderBox();
    const box = screen.getByRole('textbox');

    expect(box).toHaveAttribute('maxLength', String(maxQuestionLength));
    expect(screen.getByRole('button', { name: 'Ask' })).toBeDisabled();

    const user = userEvent.setup();
    await user.type(box, '   ');
    expect(screen.getByRole('button', { name: 'Ask' })).toBeDisabled();
    await user.keyboard('{Enter}');
    expect(post).not.toHaveBeenCalled();
    expect(screen.getByText(`3/${maxQuestionLength}`)).toBeInTheDocument();
  });

  it('aborts a question still in flight when the box goes away', async () => {
    let signal: AbortSignal | undefined;
    vi.spyOn(api, 'POST').mockImplementation(((_path: string, init: { signal: AbortSignal }) => {
      signal = init.signal;
      return new Promise(() => undefined);
    }) as never);
    const { unmount } = renderBox();

    await ask('Still there?');
    unmount();

    await waitFor(() => expect(signal?.aborted).toBe(true));
  });
});

describe('citedMoment', () => {
  const ids = new Set(['m1:412']);

  it('finds a moment by its ID, or through the event that triggered it, and nothing else', () => {
    expect(citedMoment({ kind: 'Moment', id: 'm1:412', label: '' }, ids)).toBe('m1:412');
    expect(citedMoment({ kind: 'Event', id: 'event:m1:412', label: '' }, ids)).toBe('m1:412');
    expect(citedMoment({ kind: 'Event', id: 'event:m1:500', label: '' }, ids)).toBeUndefined();
    expect(citedMoment({ kind: 'Possession', id: 'm1:412', label: '' }, ids)).toBeUndefined();
    expect(citedMoment({ kind: 'Match', id: 'm1', label: '' }, ids)).toBeUndefined();
  });
});
