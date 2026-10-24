import { blankPlayer, checkSquad, positionPlural, positions, shirtNumberOf, type PlayerRow, type TeamBuilderOptions } from './teamDraft';

interface SquadEditorProps {
  squad: PlayerRow[];
  options: TeamBuilderOptions;
  onChange: (next: PlayerRow[]) => void;
  onNameBlur: () => void;
  /** The error to show under a row's field, by server path (`squad[2].name`). */
  errorFor: (path: string) => string | undefined;
  onFillNames: () => void;
  filling: boolean;
}

/** The next free shirt number, lowest first. */
function nextShirtNumber(squad: readonly PlayerRow[], min: number, max: number): string {
  const used = new Set(squad.map((r) => shirtNumberOf(r.shirtNumber)));
  for (let n = min; n <= max; n++) if (!used.has(n)) return String(n);
  return '';
}

/**
 * The squad table: name, shirt number, player type (grouped by position) and foot, with add and remove, and live
 * rule chips that mirror the options' squad rules.
 */
export function SquadEditor({ squad, options, onChange, onNameBlur, errorFor, onFillNames, filling }: SquadEditorProps) {
  const { rules } = options;
  const check = checkSquad(squad, options);
  const update = (index: number, patch: Partial<PlayerRow>) => onChange(squad.map((row, i) => (i === index ? { ...row, ...patch } : row)));
  const remove = (index: number) => onChange(squad.filter((_, i) => i !== index));
  const add = () => onChange([...squad, blankPlayer(nextShirtNumber(squad, rules.minShirtNumber, rules.maxShirtNumber))]);
  const unnamed = squad.filter((r) => r.name.trim() === '').length;

  return (
    <fieldset className="builder-group">
      <legend className="builder-legend">Squad</legend>
      <ul className="rule-chips" aria-label="Squad rules">
        {check.chips.map((chip) => (
          <li key={chip.id} className={chip.ok ? 'pill pill-ok' : 'pill pill-bad'}>
            <span className="visually-hidden">{chip.ok ? 'Met: ' : 'Not met: '}</span>
            {chip.label}
          </li>
        ))}
      </ul>
      {errorFor('squad') && (
        <p className="field-error" role="alert">
          {errorFor('squad')}
        </p>
      )}
      <div className="squad-scroll">
        <table className="squad-table">
          <thead>
            <tr>
              <th scope="col">No.</th>
              <th scope="col">Name</th>
              <th scope="col">Type</th>
              <th scope="col">Foot</th>
              <th scope="col">
                <span className="visually-hidden">Remove</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {squad.map((row, i) => {
              const label = `Player ${i + 1}`;
              const nameError = errorFor(`squad[${i}].name`);
              const numberError = errorFor(`squad[${i}].shirtNumber`);
              const typeError = errorFor(`squad[${i}].archetype`) ?? errorFor(`squad[${i}].id`);
              return (
                <tr key={row.rowKey}>
                  <td>
                    <input
                      className="input data squad-number"
                      inputMode="numeric"
                      aria-label={`${label} shirt number`}
                      aria-invalid={numberError ? true : undefined}
                      value={row.shirtNumber}
                      onChange={(e) => update(i, { shirtNumber: e.target.value.replace(/\D/g, '').slice(0, 2) })}
                    />
                    {numberError && <span className="field-error">{numberError}</span>}
                  </td>
                  <td>
                    <input
                      className="input"
                      aria-label={`${label} name`}
                      aria-invalid={nameError ? true : undefined}
                      maxLength={rules.maxNameLength}
                      autoComplete="off"
                      value={row.name}
                      onChange={(e) => update(i, { name: e.target.value })}
                      onBlur={onNameBlur}
                    />
                    {nameError && <span className="field-error">{nameError}</span>}
                  </td>
                  <td>
                    <select
                      className="select"
                      aria-label={`${label} type`}
                      aria-invalid={typeError ? true : undefined}
                      value={row.archetype}
                      onChange={(e) => update(i, { archetype: e.target.value as PlayerRow['archetype'] })}
                    >
                      <option value="">Choose a type</option>
                      {positions.map((position) => (
                        <optgroup key={position} label={positionPlural[position]}>
                          {options.archetypes
                            .filter((a) => a.position === position && a.id)
                            .map((a) => (
                              <option key={a.id} value={a.id ?? ''} title={a.description}>
                                {a.label}
                              </option>
                            ))}
                        </optgroup>
                      ))}
                    </select>
                    {typeError && <span className="field-error">{typeError}</span>}
                  </td>
                  <td>
                    <select
                      className="select"
                      aria-label={`${label} foot`}
                      value={row.preferredFoot}
                      onChange={(e) => update(i, { preferredFoot: e.target.value as PlayerRow['preferredFoot'] })}
                    >
                      <option value="Right">Right</option>
                      <option value="Left">Left</option>
                      <option value="Both">Both</option>
                    </select>
                  </td>
                  <td>
                    <button
                      type="button"
                      className="button button-ghost button-small"
                      aria-label={`Remove ${row.name.trim() || label}`}
                      onClick={() => remove(i)}
                      disabled={squad.length <= 1}
                    >
                      Remove
                    </button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <div className="builder-actions">
        <button type="button" className="button button-ghost" onClick={add} disabled={squad.length >= rules.maxPlayers}>
          Add a player
        </button>
        <button type="button" className="button button-ghost" onClick={onFillNames} disabled={unnamed === 0 || filling}>
          {filling ? 'Finding names...' : `Suggest names for ${unnamed} unnamed`}
        </button>
      </div>
    </fieldset>
  );
}
