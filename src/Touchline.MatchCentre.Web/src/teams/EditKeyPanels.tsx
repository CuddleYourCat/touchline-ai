import { useId, useState, type FormEvent } from 'react';
import { Link } from 'react-router';
import { isEditKey, tidyEditKey } from './editKeys';
import type { TeamDetail } from './teamDraft';

/**
 * Shown once, straight after a team is created (ADR-053): the edit key, a Copy button, and whether this browser
 * remembered it. The key lives only in this component's props; leaving the page drops it from the screen for good.
 */
export function KeyReveal({ detail, editKey, remembered }: { detail: TeamDetail; editKey: string; remembered: boolean }) {
  const id = useId();
  const [copy, setCopy] = useState<'idle' | 'copied' | 'failed'>('idle');
  const teamId = encodeURIComponent(detail.team.id);

  const copyKey = async () => {
    try {
      await navigator.clipboard.writeText(editKey);
      setCopy('copied');
    } catch {
      setCopy('failed');
    }
  };

  return (
    <section className="panel key-reveal" aria-labelledby={`${id}-title`}>
      <div className="key-reveal-head">
        <img src={detail.team.crestUrl} alt="" width={64} height={64} />
        <div>
          <h1 id={`${id}-title`}>{detail.team.name} is ready</h1>
          <p className="tag">A fan-made club. Every match it plays is synthetic.</p>
        </div>
      </div>
      <h2>Your edit key</h2>
      <p>
        This key is the only way to change or delete the team. It's shown once and can't be recovered, so keep a copy if you'll
        edit from another device.
      </p>
      <div className="key-row">
        <input
          id={`${id}-key`}
          className="input data key-value"
          readOnly
          value={editKey}
          aria-label="Your edit key"
          onFocus={(e) => e.currentTarget.select()}
        />
        <button type="button" className="button" onClick={() => void copyKey()}>
          Copy
        </button>
      </div>
      <p role="status" className="muted">
        {copy === 'copied' && 'Copied. '}
        {copy === 'failed' && "Couldn't copy: select the key and copy it by hand. "}
        {remembered
          ? 'This browser remembers the key, so you can edit the team here without it.'
          : "This browser couldn't save the key, so copy it now."}
      </p>
      <div className="builder-actions">
        <Link className="button" to={`/kickoff?home=${teamId}`}>
          Play a match
        </Link>
        <Link className="button button-ghost" to={`/teams/${teamId}/edit`}>
          Edit the team
        </Link>
        <Link className="back-link" to="/teams">
          All teams
        </Link>
      </div>
    </section>
  );
}

/** Unlocks a fan team on this device with its edit key ("Enter an edit key"). */
export function EnterKeyPanel({ onKey, message }: { onKey: (key: string) => void; message?: string }) {
  const id = useId();
  const [value, setValue] = useState('');
  const [invalid, setInvalid] = useState(false);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const key = tidyEditKey(value);
    if (!isEditKey(key)) {
      setInvalid(true);
      return;
    }
    setValue('');
    setInvalid(false);
    onKey(key);
  };

  return (
    <form className="panel key-entry" onSubmit={submit} aria-labelledby={`${id}-title`}>
      <h2 id={`${id}-title`}>Enter an edit key</h2>
      <p className="muted">{message ?? 'Anyone can view this team. To change it on this device, enter the edit key you were given when it was made.'}</p>
      <div className="key-row">
        <label className="visually-hidden" htmlFor={`${id}-input`}>
          Edit key
        </label>
        <input
          id={`${id}-input`}
          className="input data key-value"
          type="password"
          autoComplete="off"
          spellCheck={false}
          value={value}
          aria-invalid={invalid ? true : undefined}
          aria-describedby={invalid ? `${id}-error` : undefined}
          onChange={(e) => setValue(e.target.value)}
        />
        <button type="submit" className="button">
          Unlock
        </button>
      </div>
      {invalid && (
        <p id={`${id}-error`} className="field-error">
          That doesn't look like an edit key. Paste the whole key.
        </p>
      )}
    </form>
  );
}
