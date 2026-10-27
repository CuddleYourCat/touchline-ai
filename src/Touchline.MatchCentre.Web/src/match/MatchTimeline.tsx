import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import type { LiveState, MatchDetail, MomentView } from '../live/matchStreamReducer';
import { MomentIcon } from './MomentIcon';
import { iconForMoment, momentIconLabel } from './momentIcons';
import { footballMinute } from './playerFacts';
import './MatchTimeline.css';

type Clock = LiveState['clock'];

export interface MatchTimelineProps {
  /** Team names come from `detail.summary.home.name` and `away.name`. */
  detail: MatchDetail;
  /** Every moment so far, any order. */
  moments: MomentView[];
  /** The live clock. */
  clock: Clock;
  fullTime: boolean;
  /** The replay's playhead, or null when watching live. */
  replayClock?: Clock | null;
  onSelectMoment: (momentId: string) => void;
  onSeek: (at: { period: Clock['period']; minute: number }) => void;
}

/** Used until the track's width is known (and where it can't be measured): icons closer than this share no lane. */
const fallbackGapPercent = 4.5;
/** The icon button's width in px; the gap between neighbours is this plus 2px. */
const iconWidth = 22;
/** The height of one row of icons, in px. */
const laneHeight = 24;

/**
 * Local copy of MomentumBars' idea: the second-half clock restarts at 45, so the second half is shifted along by the
 * first half's added time. `firstHalfEnd` is the (exclusive) end of the first half: 45, or later with added time.
 */
function layoutOf(clock: Clock, replayClock: Clock | null | undefined, moments: MomentView[]) {
  const firstHalfMinutes = [clock, ...(replayClock ? [replayClock] : []), ...moments.map((m) => m.clock)]
    .filter((c) => c.period === 'FirstHalf')
    .map((c) => c.minute + 1);
  const firstHalfEnd = Math.max(45, ...firstHalfMinutes);
  const position = (c: Clock) => (c.period === 'FirstHalf' ? c.minute : firstHalfEnd + (c.minute - 45)) + c.second / 60;
  const live = position(clock);
  const axisEnd = Math.max(firstHalfEnd + 45, live + 1);
  return { firstHalfEnd, axisEnd, position, live };
}

/** The clock for a whole-minute slider value: values before the half-time divider are first half. */
function clockAt(value: number, firstHalfEnd: number): { period: Clock['period']; minute: number } {
  return value < firstHalfEnd
    ? { period: 'FirstHalf', minute: value }
    : { period: 'SecondHalf', minute: 45 + (value - firstHalfEnd) };
}

/** Puts each icon in the first lane that has room, working along the bar. Lane 0 is nearest the bar. */
function assignLanes<T extends { percent: number }>(items: T[], minGapPercent: number): Array<T & { lane: number }> {
  const laneEnds: number[] = [];
  return [...items]
    .sort((a, b) => a.percent - b.percent)
    .map((item) => {
      let lane = laneEnds.findIndex((end) => item.percent - end >= minGapPercent);
      if (lane === -1) {
        lane = laneEnds.length;
      }
      laneEnds[lane] = item.percent;
      return { ...item, lane };
    });
}

/**
 * One bar across the match with an icon for each key moment, and a slider to replay from a point (ADR-067, ADR-068).
 * Home's moments sit above the bar and away's below it; the side, the label and the border style say which, so colour
 * isn't the only cue.
 */
export function MatchTimeline({
  detail,
  moments,
  clock,
  fullTime,
  replayClock,
  onSelectMoment,
  onSeek,
}: MatchTimelineProps) {
  const homeName = detail.summary.home.name;
  const awayName = detail.summary.away.name;
  const { firstHalfEnd, axisEnd, position, live } = useMemo(
    () => layoutOf(clock, replayClock, moments),
    [clock, replayClock, moments],
  );
  const trackRef = useRef<HTMLDivElement>(null);
  const [trackWidth, setTrackWidth] = useState<number | null>(null);
  useEffect(() => {
    const el = trackRef.current;
    if (!el) {
      return;
    }
    const measure = () => setTrackWidth(el.clientWidth > 0 ? el.clientWidth : null);
    measure();
    if (typeof ResizeObserver === 'undefined') {
      return;
    }
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);
  const gapPercent = trackWidth ? ((iconWidth + 2) / trackWidth) * 100 : fallbackGapPercent;
  const percentOf = (pos: number) => Math.min(Math.max((pos / axisEnd) * 100, 0), 100);

  const icons = useMemo(() => {
    const pct = (pos: number) => Math.min(Math.max((pos / axisEnd) * 100, 0), 100);
    const placed = moments.flatMap((moment) => {
      const kind = iconForMoment(moment.kind);
      if (kind === null) {
        return [];
      }
      return [{ moment, kind, team: moment.team, percent: pct(position(moment.clock)) }];
    });
    return {
      home: assignLanes(placed.filter((p) => p.team === 'Home'), gapPercent),
      away: assignLanes(placed.filter((p) => p.team !== 'Home'), gapPercent),
    };
  }, [moments, axisEnd, position, gapPercent]);

  const maxValue = Math.floor(live);
  const replayValue = replayClock ? Math.min(Math.floor(position(replayClock)), maxValue) : maxValue;
  const [draft, setDraft] = useState<number | null>(null);
  const value = Math.min(draft ?? replayValue, maxValue);
  const minuteText = (v: number) => footballMinute({ ...clockAt(v, firstHalfEnd), second: 0, display: '' });

  const commit = () => {
    if (draft !== null) {
      onSeek(clockAt(Math.min(draft, maxValue), firstHalfEnd));
      setDraft(null);
    }
  };

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    const steps: Record<string, number> = { ArrowLeft: -1, ArrowDown: -1, ArrowRight: 1, ArrowUp: 1 };
    const step = steps[event.key];
    if (step !== undefined) {
      event.preventDefault();
      setDraft(Math.min(Math.max(value + step, 0), maxValue));
    } else if (event.key === 'Home' || event.key === 'End') {
      event.preventDefault();
      setDraft(event.key === 'Home' ? 0 : maxValue);
    } else if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      if (draft === null) {
        onSeek(clockAt(value, firstHalfEnd));
      } else {
        commit();
      }
    }
  };

  const fillPercent = fullTime ? 100 : percentOf(live);
  const livePercent = percentOf(live);
  const sliderPercent = percentOf(maxValue);
  const laneCount = (side: typeof icons.home) => side.reduce((n, i) => Math.max(n, i.lane + 1), 1);

  const renderIcons = (side: typeof icons.home, teamName: string, away: boolean) =>
    side.map(({ moment, kind, percent, lane }) => {
      const label = `${momentIconLabel[kind]}, ${footballMinute(moment.clock)}, ${teamName}`;
      return (
        <button
          key={moment.id}
          type="button"
          className={`timeline-icon ${away ? 'timeline-icon-away' : 'timeline-icon-home'}`}
          style={{ left: `${percent}%`, [away ? 'top' : 'bottom']: lane * laneHeight }}
          aria-label={label}
          title={label}
          data-testid="timeline-icon"
          onClick={() => onSelectMoment(moment.id)}
        >
          <MomentIcon kind={kind} size={14} />
        </button>
      );
    });

  return (
    <section className="match-section match-tray timeline" aria-label="Match timeline">
      <h2>Timeline</h2>
      <p className="visually-hidden">
        {homeName} moments sit above the bar and {awayName} moments below it.
      </p>
      <div className="timeline-track" ref={trackRef}>
        <div className="timeline-icons timeline-icons-home" style={{ height: laneCount(icons.home) * laneHeight }}>
          {renderIcons(icons.home, homeName, false)}
        </div>
        <div className="timeline-bar">
          <div className="timeline-fill" data-testid="timeline-fill" style={{ width: `${fillPercent}%` }} />
          <div className="timeline-half" style={{ left: `${percentOf(firstHalfEnd)}%` }}>
            <span className="timeline-half-label">HT</span>
          </div>
          {!fullTime && (
            <div className="timeline-live" data-testid="timeline-live" style={{ left: `${livePercent}%` }}>
              <span className="timeline-live-label">Live</span>
            </div>
          )}
          {replayClock && (
            <div
              className="timeline-playhead"
              data-testid="timeline-playhead"
              role="img"
              aria-label={`Replay at ${footballMinute(replayClock)}`}
              style={{ left: `${percentOf(position(replayClock))}%` }}
            />
          )}
          <input
            className="timeline-slider"
            type="range"
            aria-label="Replay from minute"
            aria-valuetext={minuteText(value)}
            min={0}
            max={maxValue}
            step={1}
            value={value}
            style={{ width: `${sliderPercent}%` }}
            onChange={(e) => setDraft(Math.min(Number(e.target.value), maxValue))}
            onPointerUp={commit}
            onKeyDown={onKeyDown}
            onBlur={() => setDraft(null)}
          />
          {draft !== null && (
            <output
              className="timeline-readout"
              data-testid="timeline-readout"
              style={{ left: `${percentOf(draft)}%` }}
            >
              {minuteText(draft)}
            </output>
          )}
        </div>
        <div className="timeline-icons timeline-icons-away" style={{ height: laneCount(icons.away) * laneHeight }}>
          {renderIcons(icons.away, awayName, true)}
        </div>
      </div>
      <p className="timeline-hint muted">Drag or use the arrow keys, then press Enter, to replay from a minute.</p>
    </section>
  );
}
