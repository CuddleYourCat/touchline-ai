import { useMemo } from 'react';
import type { MatchDetail, MomentView } from '../live/matchStreamReducer';
import './LineupsCard.css';
import { MomentIcon } from './MomentIcon';
import { momentIconLabel } from './momentIcons';
import { footballMinute, playerFacts, type PlayerFact } from './playerFacts';

type Player = MatchDetail['homeLineup'][number];

export interface LineupsCardProps {
  detail: MatchDetail;
  /** Every moment so far, any order. */
  moments: MomentView[];
  onSelectPlayer: (playerId: string) => void;
  selectedPlayerId?: string | null;
}


function positionLabel(position: Player['position']): string {
  switch (position) {
    case 'Goalkeeper':
      return 'GK';
    case 'Defender':
      return 'DF';
    case 'Midfielder':
      return 'MF';
    case 'Forward':
      return 'FW';
    default:
      return '';
  }
}

/** Both teams' starting elevens and benches, with what each player did (ADR-068). */
export function LineupsCard({ detail, moments, onSelectPlayer, selectedPlayerId }: LineupsCardProps) {
  const facts = useMemo(() => playerFacts(moments), [moments]);

  const team = (name: string, starters: Player[], bench: Player[]) => (
    <section className="lineups-team" aria-label={`${name} line-up`}>
      <h3 className="lineups-team-name">{name}</h3>
      <PlayerList players={starters} facts={facts} onSelectPlayer={onSelectPlayer} selectedPlayerId={selectedPlayerId} />
      <h4 className="lineups-subheading">Substitutes</h4>
      <PlayerList
        players={bench}
        facts={facts}
        onSelectPlayer={onSelectPlayer}
        selectedPlayerId={selectedPlayerId}
        bench
      />
    </section>
  );

  return (
    <div className="lineups-card">
      {team(detail.summary.home.name, detail.homeLineup, detail.homeSubstitutes)}
      {team(detail.summary.away.name, detail.awayLineup, detail.awaySubstitutes)}
    </div>
  );
}

interface PlayerListProps {
  players: Player[];
  facts: Record<string, PlayerFact[]>;
  onSelectPlayer: (playerId: string) => void;
  selectedPlayerId?: string | null;
  bench?: boolean;
}

function PlayerList({ players, facts, onSelectPlayer, selectedPlayerId, bench = false }: PlayerListProps) {
  return (
    <ul className="lineups-list">
      {players.map((player) => {
        const own = facts[player.id] ?? [];
        const unused = bench && !own.some((f) => f.kind === 'subOn');
        const selected = selectedPlayerId === player.id;
        const className = ['lineups-row', unused ? 'lineups-row-unused' : '', selected ? 'lineups-row-selected' : '']
          .filter(Boolean)
          .join(' ');
        return (
          <li key={player.id} className={className}>
            <span className="lineups-number">{player.shirtNumber}</span>
            <button
              type="button"
              className="lineups-name"
              aria-pressed={selected}
              onClick={() => onSelectPlayer(player.id)}
            >
              {player.name}
            </button>
            <span className="lineups-position">{positionLabel(player.position)}</span>
            <span className="lineups-facts">
              {own.map((fact) => {
                const minute = footballMinute(fact.clock);
                return (
                  <span key={`${fact.momentId}:${fact.kind}`} className="lineups-fact">
                    <MomentIcon kind={fact.kind} size={14} label={`${momentIconLabel[fact.kind]}, ${minute}`} />
                    <span className="lineups-minute" aria-hidden="true">
                      {minute}
                    </span>
                  </span>
                );
              })}
            </span>
          </li>
        );
      })}
    </ul>
  );
}
