import { memo, useMemo } from 'react';
import {
  pitchLength,
  pitchWidth,
  type BallPath,
  type MatchDetail,
  type MatchEvent,
  type OverlayCard,
  type ShapeView,
} from '../live/matchStreamReducer';
import { OverlayToast } from './OverlayToast';
import { ballPathD, ballTrail, incidentMarkers, isReplayed, toSvg, trailLayers, unknownPlayerLabel } from './pitchGeometry';
import { useReducedMotion } from './useReducedMotion';

type Player = MatchDetail['homeLineup'][number];

/**
 * What the pitch shows: the latest modelled shape, the latest ball path, the recent events and the overlay cards.
 * Every prop is data, not a React element, so the memoised pitch skips renders when only other parts of the match
 * change.
 */
export interface PitchProps {
  shape: ShapeView | null;
  ballPath: BallPath | null;
  events: MatchEvent[];
  homeLineup: Player[];
  awayLineup: Player[];
  /** The benches (ADR-064), so a substitute who comes on is labelled like a starter. */
  homeBench?: Player[];
  awayBench?: Player[];
  /** Overlay cards, the newest drawn as a toast over the pitch. */
  overlays?: OverlayCard[];
  homeName?: string;
  awayName?: string;
}

// One empty bench for every pitch without one, so the default doesn't defeat the memo.
const noBench: Player[] = [];

const half = pitchWidth / 2;
const boxWidth = 40.32;
const sixWidth = 18.32;
const goalWidth = 7.32;

/** The lines of a 105 m by 68 m pitch, drawn once. */
const Markings = memo(function Markings() {
  const end = (x: number, dir: 1 | -1) => {
    const box = 16.5 * dir;
    const six = 5.5 * dir;
    return (
      <g key={x}>
        <rect x={Math.min(x, x + box)} y={half - boxWidth / 2} width={16.5} height={boxWidth} />
        <rect x={Math.min(x, x + six)} y={half - sixWidth / 2} width={5.5} height={sixWidth} />
        <rect x={dir === 1 ? x - 2 : x} y={half - goalWidth / 2} width={2} height={goalWidth} />
        <circle cx={x + 11 * dir} cy={half} r={0.3} className="pitch-spot" />
      </g>
    );
  };
  return (
    <g className="pitch-lines">
      <rect x={0} y={0} width={pitchLength} height={pitchWidth} />
      <line x1={pitchLength / 2} y1={0} x2={pitchLength / 2} y2={pitchWidth} />
      <circle cx={pitchLength / 2} cy={half} r={9.15} />
      <circle cx={pitchLength / 2} cy={half} r={0.3} className="pitch-spot" />
      {end(0, 1)}
      {end(pitchLength, -1)}
    </g>
  );
});

/** The key to the pitch markers, drawn with the same shapes and classes as the markers themselves. */
const PitchLegend = memo(function PitchLegend() {
  return (
    <ul className="pitch-legend" aria-label="Pitch key">
      <li>
        <svg viewBox="-1.2 -1.2 2.4 2.4" width="12" height="12" aria-hidden="true">
          <path className="marker-foul" d="M -0.9 -0.9 L 0.9 0.9 M 0.9 -0.9 L -0.9 0.9" />
        </svg>
        Foul
      </li>
      <li>
        <svg viewBox="-1.2 -1.2 2.4 2.4" width="12" height="12" aria-hidden="true">
          <rect className="marker-card marker-yellow" x={-0.6} y={-0.9} width={1.2} height={1.8} />
        </svg>
        Yellow card
      </li>
      <li>
        <svg viewBox="-1.2 -1.2 2.4 2.4" width="12" height="12" aria-hidden="true">
          <rect className="marker-card marker-red" x={-0.6} y={-0.9} width={1.2} height={1.8} />
        </svg>
        Red card
      </li>
    </ul>
  );
});

/**
 * The live pitch (ADR-035): player dots from the server's shape model, the ball's latest path with a short fading
 * trail, and markers for fouls and cards. Home attacks left to right. Positions are modelled, not tracked.
 */
export const Pitch = memo(function Pitch({
  shape,
  ballPath,
  events,
  homeLineup,
  awayLineup,
  homeBench = noBench,
  awayBench = noBench,
  overlays,
  homeName = '',
  awayName = '',
}: PitchProps) {
  const reducedMotion = useReducedMotion();
  const players = useMemo(
    () => new Map([...homeLineup, ...awayLineup, ...homeBench, ...awayBench].map((p) => [p.id, p])),
    [homeLineup, awayLineup, homeBench, awayBench],
  );
  const trailFrom = ballPath?.from;
  const trail = useMemo(() => trailLayers(trailFrom ? [...ballTrail(events), trailFrom] : ballTrail(events)), [events, trailFrom]);
  const markers = useMemo(() => incidentMarkers(events), [events]);
  const d = ballPath ? ballPathD(ballPath) : null;
  const ballAt = ballPath ? toSvg(ballPath.to) : null;
  const replayed = ballPath !== null && isReplayed(ballPath, shape);

  return (
    <section className="match-section pitch-panel" aria-label="Pitch">
      <figure className="pitch-figure">
        <div className="pitch-frame">
          <svg
            className={reducedMotion ? 'pitch pitch-still' : 'pitch'}
            viewBox={`-3 -3 ${pitchLength + 6} ${pitchWidth + 6}`}
            preserveAspectRatio="xMidYMid meet"
            role="img"
            aria-label="Pitch view, home attacking left to right"
          >
            <Markings />
            <g className="pitch-markers">
              {markers.map((m) => {
                const at = toSvg(m.at);
                return m.kind === 'foul' ? (
                  <path
                    key={m.sequence}
                    data-testid="marker-foul"
                    className="marker-foul"
                    d={`M ${at.x - 0.9} ${at.y - 0.9} L ${at.x + 0.9} ${at.y + 0.9} M ${at.x + 0.9} ${at.y - 0.9} L ${at.x - 0.9} ${at.y + 0.9}`}
                  >
                    <title>Foul</title>
                  </path>
                ) : (
                  <rect
                    key={m.sequence}
                    data-testid={`marker-${m.kind}`}
                    className={`marker-card marker-${m.kind}`}
                    x={at.x - 0.6}
                    y={at.y - 0.9}
                    width={1.2}
                    height={1.8}
                  >
                    <title>{m.kind === 'red' ? 'Red card' : 'Yellow card'}</title>
                  </rect>
                );
              })}
            </g>
            <g className="pitch-players">
              {shape?.positions.map((p) => {
                const at = toSvg(p);
                const player = players.get(p.playerId);
                const label = player ? String(player.shirtNumber) : unknownPlayerLabel(p.playerId);
                return (
                  <g
                    key={p.playerId}
                    data-testid="player-dot"
                    data-player={p.playerId}
                    className={p.team === 'Home' ? 'player player-home' : 'player player-away'}
                    style={{ transform: `translate(${at.x}px, ${at.y}px)` }}
                  >
                    <title>{player ? `${player.shirtNumber} ${player.name}` : 'Player not in the line-up'}</title>
                    <circle r={1.7} />
                    <text dy="0.6">{label}</text>
                  </g>
                );
              })}
            </g>
            <g className="pitch-ball">
              {trail.length > 0 && (
                <g data-testid="ball-trail" className="ball-trail">
                  {trail.map((layer, i) => (
                    <path key={i} data-testid="ball-trail-layer" d={layer} />
                  ))}
                </g>
              )}
              {ballPath && d && (
                <path key={`p${ballPath.sequence}`} data-testid="ball-path" data-kind={ballPath.kind} className={`ball-path ball-path-${ballPath.kind}`} d={d} />
              )}
              {ballPath && ballAt &&
                (reducedMotion || !d || replayed ? (
                  <circle data-testid="ball" className="ball" cx={ballAt.x} cy={ballAt.y} r={0.8} />
                ) : (
                  <circle key={`b${ballPath.sequence}`} data-testid="ball" className="ball" r={0.8}>
                    <animateMotion dur="0.9s" fill="freeze" calcMode="spline" keyTimes="0;1" keySplines="0.3 0 0.2 1" path={d} />
                  </circle>
                ))}
            </g>
          </svg>
          {overlays && <OverlayToast overlays={overlays} homeName={homeName} awayName={awayName} />}
        </div>
        <figcaption className="pitch-caption muted">
          <span>Modelled positions, not tracking data</span>
          <PitchLegend />
        </figcaption>
      </figure>
    </section>
  );
});
