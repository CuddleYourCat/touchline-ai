import { memo, useMemo } from 'react';
import type { MomentumReading } from '../live/matchStreamReducer';

/** The bars' height either side of the zero line, in SVG units. */
const reach = 20;

/**
 * Lays the windows out on one axis. The second-half clock restarts at 45, so first-half added-time windows (45-50 in
 * the first half) and the second half's opening windows share minutes. The second half is shifted along by the first
 * half's added time: a second-half window starts at `firstHalfEnd + (fromMinute - 45)`, where `firstHalfEnd` is the
 * last first-half minute (45 when there's no added time yet).
 */
function momentumLayout(readings: MomentumReading[]) {
  const sorted = [...readings].sort(
    (a, b) => (a.period === b.period ? 0 : a.period === 'FirstHalf' ? -1 : 1) || a.fromMinute - b.fromMinute,
  );
  const firstHalfEnd = Math.max(45, ...sorted.filter((r) => r.period === 'FirstHalf').map((r) => r.toMinute));
  const offset = (r: MomentumReading, minute: number) => (r.period === 'FirstHalf' ? minute : firstHalfEnd + (minute - 45));
  const bars = sorted.map((r) => ({ reading: r, x: offset(r, r.fromMinute), end: offset(r, r.toMinute) }));
  const axisEnd = Math.max(firstHalfEnd + 45, ...bars.map((b) => b.end));
  return { bars, firstHalfEnd, axisEnd };
}

/**
 * Five-minute momentum windows as diverging bars (ADR-035): home threat rises above the zero line, away threat falls
 * below it. Hand-rolled SVG, so there's no chart dependency. Memoised, so a message that doesn't touch momentum
 * doesn't re-render it.
 */
export const MomentumBars = memo(function MomentumBars({
  readings,
  homeName,
  awayName,
}: {
  readings: MomentumReading[];
  homeName: string;
  awayName: string;
}) {
  const { bars, firstHalfEnd, axisEnd } = useMemo(() => momentumLayout(readings), [readings]);

  return (
    <section className="match-section momentum" aria-label="Momentum">
      <h2>Momentum</h2>
      {bars.length === 0 ? (
        <p className="muted">No momentum readings yet.</p>
      ) : (
        <>
          <svg
            className="momentum-chart"
            viewBox={`0 ${-reach} ${axisEnd} ${reach * 2}`}
            preserveAspectRatio="none"
            role="img"
            aria-label={`Momentum by five-minute window, ${homeName} above the line, ${awayName} below`}
          >
            <line className="momentum-zero" x1={0} y1={0} x2={axisEnd} y2={0} />
            <line data-testid="momentum-half" className="momentum-half" x1={firstHalfEnd} y1={-reach} x2={firstHalfEnd} y2={reach} />
            {bars.map(({ reading: r, x, end }) => {
              const h = Math.min(Math.abs(r.value), 1) * reach;
              const home = r.value >= 0;
              return (
                <rect
                  key={`${r.period}:${r.fromMinute}`}
                  data-testid="momentum-bar"
                  className={home ? 'momentum-bar momentum-home' : 'momentum-bar momentum-away'}
                  x={x + 0.4}
                  y={home ? -h : 0}
                  width={Math.max(end - x - 0.8, 0.5)}
                  height={h}
                >
                  <title>{`${r.fromMinute}-${r.toMinute}': ${home ? homeName : awayName} ${Math.round(Math.abs(r.value) * 100)}%`}</title>
                </rect>
              );
            })}
          </svg>
          <div className="momentum-legend muted">
            <span className="momentum-key momentum-key-home">{homeName}</span>
            <span className="momentum-key momentum-key-away">{awayName}</span>
          </div>
        </>
      )}
    </section>
  );
});
