import { useCallback, useEffect, useId, useRef, useState, type FormEvent } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { describeWait, type FieldErrors } from '../api/problems';
import { CrestPicker } from '../teams/CrestPicker';
import { EnterKeyPanel, KeyReveal } from '../teams/EditKeyPanels';
import { editKeyFor, forgetEditKey, rememberEditKey } from '../teams/editKeys';
import { SquadEditor } from '../teams/SquadEditor';
import {
  checkSquad,
  draftFromForm,
  fieldLabel,
  formFromDetail,
  nameCheckDelay,
  namesNeedingCheck,
  newTeamForm,
  refusedName,
  verdictKey,
  type NameVerdict,
  type TeamBuilderOptions,
  type TeamDetail,
  type TeamForm,
} from '../teams/teamDraft';
import { checkNames, createTeam, deleteTeam, replaceTeam, suggestNames, type Forbidden, type SuggestKind } from '../teams/teamsApi';
import { useTeamBuilder } from '../teams/useTeams';

/** A message above the form: `ok` for a save, `alert` for a problem. `reload` offers to reload the team. */
interface Notice {
  tone: 'ok' | 'alert';
  text: string;
  reload?: boolean;
}

const genericFailure = "We couldn't reach the Match Centre just now. Your changes are still here, so try again.";
const namesUnavailable = "Names can't be checked right now. Nothing was saved; try again in a moment.";

interface NameFieldProps {
  label: string;
  value: string;
  onChange: (value: string) => void;
  onBlur: () => void;
  error?: string;
  maxLength: number;
  hint?: string;
  onSuggest?: () => void;
  suggesting?: boolean;
  className?: string;
}

/** A labelled name input with an optional Suggest button, a hint and its error. */
function NameField({ label, value, onChange, onBlur, error, maxLength, hint, onSuggest, suggesting, className }: NameFieldProps) {
  const id = useId();
  const described = [hint ? `${id}-hint` : '', error ? `${id}-error` : ''].filter(Boolean).join(' ') || undefined;
  return (
    <div className="builder-field">
      <label className="viewer-label" htmlFor={id}>
        {label}
      </label>
      <div className="name-row">
        <input
          id={id}
          className={`input ${className ?? ''}`}
          value={value}
          maxLength={maxLength}
          autoComplete="off"
          aria-invalid={error ? true : undefined}
          aria-describedby={described}
          onChange={(e) => onChange(e.target.value)}
          onBlur={onBlur}
        />
        {onSuggest && (
          <button type="button" className="button button-ghost button-small" onClick={onSuggest} disabled={suggesting} aria-label={`Suggest a ${label.toLowerCase()}`}>
            {suggesting ? '...' : 'Suggest'}
          </button>
        )}
      </div>
      {hint && (
        <span id={`${id}-hint`} className="tag">
          {hint}
        </span>
      )}
      {error && (
        <span id={`${id}-error`} className="field-error">
          {error}
        </span>
      )}
    </div>
  );
}

interface TeamEditorProps {
  options: TeamBuilderOptions;
  /** The team being edited, or null to build a new one. */
  detail: TeamDetail | null;
  onCreated: (created: { detail: TeamDetail; editKey: string; remembered: boolean }) => void;
  onReload: () => void;
}

/**
 * The builder form, for a new team or a fan team. Its state starts from props once: the page remounts it (with a new
 * `key`) after a reload, which is React's way to reset state from new props.
 */
function TeamEditor({ options, detail, onCreated, onReload }: TeamEditorProps) {
  const navigate = useNavigate();
  const teamId = detail?.team.id;
  const [form, setForm] = useState<TeamForm>(() => (detail ? formFromDetail(detail) : newTeamForm(options)));
  const [version, setVersion] = useState(detail?.version ?? 0);
  // A stored key counts as verified: it came from creating the team or from a save that worked. A pasted key is
  // held here unverified, and only stored once the service accepts it (ADR-053 review fixes).
  const [editKey, setEditKey] = useState<{ value: string; verified: boolean } | undefined>(() => {
    const value = teamId ? editKeyFor(teamId) : undefined;
    return value ? { value, verified: true } : undefined;
  });
  const [stored, setStored] = useState(() => (teamId ? editKeyFor(teamId) !== undefined : false));
  const [keyMessage, setKeyMessage] = useState<string>();
  const [errors, setErrors] = useState<FieldErrors>({});
  const [verdicts, setVerdicts] = useState<ReadonlyMap<string, NameVerdict>>(() => new Map());
  const [checkNote, setCheckNote] = useState<string>();
  const [busy, setBusy] = useState<'saving' | 'deleting' | undefined>();
  const [notice, setNotice] = useState<Notice>();
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [suggesting, setSuggesting] = useState<SuggestKind>();
  const editable = !teamId || editKey !== undefined;

  // Name checks read the latest form when they fire, not the one from the blur that scheduled them.
  const latest = useRef({ form, verdicts });
  useEffect(() => {
    latest.current = { form, verdicts };
  }, [form, verdicts]);
  const checkTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const lastCheckAt = useRef<number | undefined>(undefined);
  useEffect(() => {
    const timer = checkTimer;
    return () => clearTimeout(timer.current);
  }, []);

  const runNameCheck = useCallback(async () => {
    checkTimer.current = undefined;
    const pending = namesNeedingCheck(latest.current.form, latest.current.verdicts);
    if (pending.length === 0) return;
    lastCheckAt.current = Date.now();
    const outcome = await checkNames(pending);
    if (outcome.kind === 'ok') {
      setVerdicts((previous) => {
        const next = new Map(previous);
        pending.forEach((sent, i) => {
          const verdict = outcome.verdicts[i];
          if (verdict) next.set(verdictKey(sent.kind, sent.name), verdict);
        });
        return next;
      });
      setCheckNote(undefined);
    } else if (outcome.kind === 'namesUnavailable') {
      setCheckNote(namesUnavailable);
    } else if (outcome.kind === 'rateLimited') {
      // Hold further checks until the limiter's window has passed.
      lastCheckAt.current = Date.now() + (outcome.retryAfterSeconds ?? 60) * 1000;
      setCheckNote('Name checks are paused for a moment. Every name is checked again when you save.');
    } else {
      setCheckNote("Names couldn't be checked just now. Every name is checked again when you save.");
    }
  }, []);

  /** Batches name checks: a blur schedules one check for every unchecked name, never one per keystroke. */
  const scheduleNameCheck = useCallback(() => {
    if (checkTimer.current !== undefined) return;
    if (namesNeedingCheck(latest.current.form, latest.current.verdicts).length === 0) return;
    checkTimer.current = setTimeout(() => void runNameCheck(), nameCheckDelay(Date.now(), lastCheckAt.current));
  }, [runNameCheck]);

  const errorFor = (path: string) => errors[path]?.join(' ') ?? refusedName(form, path, verdicts);
  const set = (patch: Partial<TeamForm>) => setForm((f) => ({ ...f, ...patch }));

  const suggest = async (kind: SuggestKind, apply: (name: string) => void) => {
    setSuggesting(kind);
    const names = await suggestNames(kind, 1);
    setSuggesting(undefined);
    const name = names?.[0];
    if (name) apply(name);
    else setNotice({ tone: 'alert', text: 'No name suggestions just now. Try again, or type your own.' });
  };

  const fillPlayerNames = async () => {
    const unnamed = form.squad.filter((r) => r.name.trim() === '').length;
    if (unnamed === 0) return;
    setSuggesting('player');
    const names = await suggestNames('player', Math.min(unnamed, 10));
    setSuggesting(undefined);
    if (!names || names.length === 0) {
      setNotice({ tone: 'alert', text: 'No name suggestions just now. Try again, or type your own.' });
      return;
    }
    // The updater must be pure: StrictMode calls it twice, so it counts locally instead of draining a shared queue.
    setForm((f) => {
      let next = 0;
      return { ...f, squad: f.squad.map((r) => (r.name.trim() === '' && next < names.length ? { ...r, name: names[next++] ?? '' } : r)) };
    });
  };

  /** A refused key is never deleted from storage here: only the fan's "Forget this key" button does that. */
  const keyRefused = (outcome: Forbidden) => {
    if (outcome.kind === 'readOnly') {
      setNotice({ tone: 'alert', text: "This team is read-only. Built-in clubs can't be changed." });
      return;
    }
    if (outcome.kind === 'forbidden') {
      setNotice({ tone: 'alert', text: "We couldn't verify the edit key just now. Nothing was changed and the key is kept, so try again." });
      return;
    }
    setEditKey(undefined);
    setKeyMessage("That edit key doesn't match this team. Enter the team's key, then save again: your changes are still here.");
    setNotice({ tone: 'alert', text: "That edit key doesn't match this team. Enter its key at the top of the page." });
  };

  const save = async (e: FormEvent) => {
    e.preventDefault();
    if (busy) return;
    if (!checkSquad(form.squad, options).ok) {
      setNotice({ tone: 'alert', text: "The squad doesn't meet the rules yet. The red checks above the squad say what's missing." });
      return;
    }
    setBusy('saving');
    setErrors({});
    setNotice(undefined);
    const draft = draftFromForm(form);

    if (!teamId) {
      const outcome = await createTeam(draft);
      setBusy(undefined);
      switch (outcome.kind) {
        case 'created':
          onCreated({ detail: outcome.detail, editKey: outcome.editKey, remembered: rememberEditKey(outcome.detail.team.id, outcome.editKey) });
          return;
        case 'invalid':
          setErrors(outcome.errors);
          setNotice({ tone: 'alert', text: 'Some details need changing. They are marked below.' });
          return;
        case 'rateLimited':
          setNotice({
            tone: 'alert',
            text: `Lots of teams have been built from here recently. Try again in ${describeWait(outcome.retryAfterSeconds ?? 3600)}.`,
          });
          return;
        case 'namesUnavailable':
          setNotice({ tone: 'alert', text: namesUnavailable });
          return;
        case 'error':
          setNotice({ tone: 'alert', text: genericFailure });
          return;
      }
    }

    if (!editKey) {
      setBusy(undefined);
      return;
    }
    const outcome = await replaceTeam(teamId, draft, editKey.value, version);
    setBusy(undefined);
    switch (outcome.kind) {
      case 'saved':
        if (!editKey.verified) {
          setStored(rememberEditKey(teamId, editKey.value));
          setEditKey({ value: editKey.value, verified: true });
        }
        setForm(formFromDetail(outcome.detail));
        setVersion(outcome.detail.version);
        setNotice({ tone: 'ok', text: 'Saved.' });
        return;
      case 'invalid':
        setErrors(outcome.errors);
        setNotice({ tone: 'alert', text: 'Some details need changing. They are marked below.' });
        return;
      case 'stale':
        setNotice({ tone: 'alert', text: 'This team changed since you loaded it. Reload it to see the latest, then make your changes again.', reload: true });
        return;
      case 'readOnly':
      case 'wrongKey':
      case 'forbidden':
        keyRefused(outcome);
        return;
      case 'notFound':
        forgetEditKey(teamId);
        setStored(false);
        setNotice({ tone: 'alert', text: 'This team no longer exists.' });
        return;
      case 'namesUnavailable':
        setNotice({ tone: 'alert', text: namesUnavailable });
        return;
      case 'error':
        setNotice({ tone: 'alert', text: genericFailure });
        return;
    }
  };

  const remove = async () => {
    if (!teamId || !editKey || busy) return;
    setBusy('deleting');
    setNotice(undefined);
    const outcome = await deleteTeam(teamId, editKey.value);
    setBusy(undefined);
    setConfirmDelete(false);
    switch (outcome.kind) {
      case 'deleted':
      case 'notFound':
        forgetEditKey(teamId);
        void navigate('/teams');
        return;
      case 'playing':
        setNotice({ tone: 'alert', text: "This team is playing a match right now, so it can't be deleted. Try again after full time." });
        return;
      case 'readOnly':
      case 'wrongKey':
      case 'forbidden':
        keyRefused(outcome);
        return;
      case 'error':
        setNotice({ tone: 'alert', text: genericFailure });
        return;
    }
  };

  const forgetKey = () => {
    if (!teamId) return;
    forgetEditKey(teamId);
    setStored(false);
    setEditKey(undefined);
    setKeyMessage(undefined);
    setConfirmDelete(false);
  };

  const unlock = (key: string) => {
    if (!teamId) return;
    setEditKey({ value: key, verified: false });
    setKeyMessage(undefined);
    setNotice(undefined);
  };

  const errorList = Object.entries(errors);
  const maxName = options.rules.maxNameLength;
  const style = options.managerStyles.find((s) => s.id === form.manager.style);

  return (
    <div className="builder">
      <section className="panel builder-head">
        <div>
          <h1>{teamId ? `Edit ${detail?.team.name ?? 'team'}` : 'Build a team'}</h1>
          <p className="muted">
            {teamId
              ? 'A fan-made club. Changes apply to its next match.'
              : 'Make a fictional club: its name, crest, manager and squad. Real clubs and players are not allowed.'}
          </p>
        </div>
        <div className="builder-head-tags">
          <span className="pill">fan-made</span>
          {teamId && stored && (
            <button type="button" className="button button-ghost button-small" onClick={forgetKey}>
              Forget this key on this device
            </button>
          )}
        </div>
      </section>

      {teamId && !editKey && <EnterKeyPanel onKey={unlock} message={keyMessage} />}

      <div role="status" className="builder-status">
        {checkNote && <p className="tag">{checkNote}</p>}
      </div>
      {errorList.length > 0 && (
        <ul className="panel error-summary" aria-label="What needs changing">
          {errorList.map(([path, messages]) => (
            <li key={path}>
              <strong>{fieldLabel(path)}:</strong> {messages.join(' ')}
            </li>
          ))}
        </ul>
      )}

      <form className="panel builder-form" onSubmit={(e) => void save(e)} noValidate aria-label={teamId ? 'Edit team' : 'New team'}>
        <fieldset className="builder-lock" disabled={!editable || busy !== undefined}>
          <fieldset className="builder-group">
            <legend className="builder-legend">Club</legend>
            <div className="builder-fields">
              <NameField
                label="Club name"
                value={form.name}
                maxLength={maxName}
                onChange={(name) => set({ name })}
                onBlur={scheduleNameCheck}
                error={errorFor('name')}
                onSuggest={() => void suggest('team', (name) => set({ name }))}
                suggesting={suggesting === 'team'}
              />
              <NameField
                label="Short name"
                value={form.shortName}
                maxLength={4}
                className="data"
                hint="2 to 4 capital letters, for the scoreboard"
                onChange={(v) => set({ shortName: v.toUpperCase().replace(/[^A-Z]/g, '') })}
                onBlur={scheduleNameCheck}
                error={errorFor('shortName')}
              />
              <NameField
                label="Ground"
                value={form.venue}
                maxLength={maxName}
                onChange={(venue) => set({ venue })}
                onBlur={scheduleNameCheck}
                error={errorFor('venue')}
                onSuggest={() => void suggest('venue', (venue) => set({ venue }))}
                suggesting={suggesting === 'venue'}
              />
              <label className="builder-field builder-colour">
                <span className="viewer-label">Main kit colour</span>
                <input type="color" value={form.colours.primary} onChange={(e) => set({ colours: { ...form.colours, primary: e.target.value } })} />
              </label>
              <label className="builder-field builder-colour">
                <span className="viewer-label">Trim kit colour</span>
                <input type="color" value={form.colours.secondary} onChange={(e) => set({ colours: { ...form.colours, secondary: e.target.value } })} />
                {errorFor('colours.secondary') && <span className="field-error">{errorFor('colours.secondary')}</span>}
              </label>
            </div>
          </fieldset>

          <CrestPicker
            crest={form.crest}
            options={options}
            onChange={(crest) => set({ crest })}
            onNameBlur={scheduleNameCheck}
            initialsError={errorFor('crest.initials')}
          />

          <fieldset className="builder-group">
            <legend className="builder-legend">Manager</legend>
            <div className="builder-fields">
              <NameField
                label="Manager name"
                value={form.manager.name}
                maxLength={maxName}
                onChange={(name) => set({ manager: { ...form.manager, name } })}
                onBlur={scheduleNameCheck}
                error={errorFor('manager.name')}
                onSuggest={() => void suggest('manager', (name) => setForm((f) => ({ ...f, manager: { ...f.manager, name } })))}
                suggesting={suggesting === 'manager'}
              />
              <label className="builder-field">
                <span className="viewer-label">Style</span>
                <select
                  className="select"
                  value={form.manager.style}
                  onChange={(e) => set({ manager: { ...form.manager, style: e.target.value as TeamForm['manager']['style'] } })}
                >
                  {options.managerStyles.map((s) => (
                    <option key={s.id} value={s.id}>
                      {s.label}
                    </option>
                  ))}
                </select>
                {style && <span className="tag">{style.description}</span>}
              </label>
            </div>
          </fieldset>

          <SquadEditor
            squad={form.squad}
            options={options}
            onChange={(squad) => {
              set({ squad });
              if (squad.length !== form.squad.length) setErrors({});
            }}
            onNameBlur={scheduleNameCheck}
            errorFor={errorFor}
            onFillNames={() => void fillPlayerNames()}
            filling={suggesting === 'player'}
          />

          {/* Next to the buttons, so the outcome of a save shows where the fan clicked. */}
          <div role="status" className="builder-status">
            {notice?.tone === 'ok' && <p className="banner banner-quiet">{notice.text}</p>}
          </div>
          {notice?.tone === 'alert' && (
            <div role="alert" className="banner builder-alert">
              <p>{notice.text}</p>
              {notice.reload && (
                <button type="button" className="button" onClick={onReload}>
                  Reload the team
                </button>
              )}
            </div>
          )}
          <div className="builder-actions">
            <button type="submit" className="button">
              {busy === 'saving' ? 'Saving...' : teamId ? 'Save changes' : 'Create team'}
            </button>
            {teamId && !confirmDelete && (
              <button type="button" className="button button-ghost" onClick={() => setConfirmDelete(true)}>
                Delete team
              </button>
            )}
            <Link className="back-link" to="/teams">
              Back to teams
            </Link>
          </div>
          {teamId && confirmDelete && (
            <div className="banner builder-alert" role="group" aria-label="Confirm delete">
              <p>Delete {form.name || 'this team'} for good? Its matches stay, but the team can't be played or edited again.</p>
              <div className="builder-actions">
                <button type="button" className="button button-danger" onClick={() => void remove()}>
                  {busy === 'deleting' ? 'Deleting...' : 'Yes, delete it'}
                </button>
                <button type="button" className="button button-ghost" onClick={() => setConfirmDelete(false)}>
                  Keep it
                </button>
              </div>
            </div>
          )}
        </fieldset>
      </form>
    </div>
  );
}

/** A built-in club: read-only (ADR-031). */
function BuiltInNotice({ detail }: { detail: TeamDetail }) {
  const id = encodeURIComponent(detail.team.id);
  return (
    <section className="panel">
      <h1>{detail.team.name}</h1>
      <p className="muted">Built-in clubs can't be changed. Build your own instead, or play a match with this one.</p>
      <div className="builder-actions">
        <Link className="button" to="/teams/new">
          Build a team
        </Link>
        <Link className="button button-ghost" to={`/kickoff?home=${id}`}>
          Play
        </Link>
      </div>
    </section>
  );
}

function TeamBuilder({ teamId }: { teamId: string | undefined }) {
  const { state, reload } = useTeamBuilder(teamId);
  const [created, setCreated] = useState<{ detail: TeamDetail; editKey: string; remembered: boolean }>();

  if (created) return <KeyReveal detail={created.detail} editKey={created.editKey} remembered={created.remembered} />;

  switch (state.kind) {
    case 'loading':
      return (
        <section className="panel" aria-busy="true">
          <p className="muted">Loading the team builder...</p>
        </section>
      );
    case 'notFound':
      return (
        <section className="panel">
          <h1>Team not found</h1>
          <p className="muted">
            There's no club with that ID. It may have been deleted. <Link to="/teams">Back to the teams</Link>.
          </p>
        </section>
      );
    case 'error':
      return (
        <section className="panel" role="alert">
          <p>We couldn't load the team builder just now.</p>
          <button type="button" className="button" onClick={reload}>
            Try again
          </button>
        </section>
      );
    case 'ready':
      if (state.detail?.team.origin === 'BuiltIn') return <BuiltInNotice detail={state.detail} />;
      return <TeamEditor key={state.load} options={state.options} detail={state.detail} onCreated={setCreated} onReload={reload} />;
  }
}

/** `/teams/new` and `/teams/:id/edit`: the team builder (ADR-031, ADR-053). Keyed by ID so a new route starts fresh. */
export function TeamEditPage() {
  const { id } = useParams();
  return <TeamBuilder key={id ?? 'new'} teamId={id} />;
}
