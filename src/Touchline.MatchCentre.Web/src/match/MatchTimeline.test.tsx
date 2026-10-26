import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { MatchDetail, MomentView } from '../live/matchStreamReducer';
import { MatchTimeline, type MatchTimelineProps } from './MatchTimeline';

type Clock = MatchTimelineProps['clock'];

const detail = { summary: { home: { name: 'North Bank' }, away: { name: 'Harbour Town' } } } as MatchDetail;

function clockOf(minute: number): Clock {
  return { period: minute < 45 ? 'FirstHalf' : 'SecondHalf', minute, second: 0, display: `${minute}:00` };
}

function moment(sequence: number, kind: string, minute: number, team: 'Home' | 'Away' = 'Home'): MomentView {
  return {
    id: `m:${sequence}`,
    tier: 'Key',
    kind,
    sequence,
    clock: clockOf(minute),
    team,
    playerId: null,
    reason: kind,
    commentary: null,
    relatedPlayerId: null,
  } as MomentView;
}

function renderTimeline(props: Partial<MatchTimelineProps> = {}) {
  const onSelectMoment = vi.fn();
  const onSeek = vi.fn();
  const utils = render(
    <MatchTimeline
      detail={detail}
      moments={[]}
      clock={clockOf(67)}
      fullTime={false}
      onSelectMoment={onSelectMoment}
      onSeek={onSeek}
      {...props}
    />,
  );
  return { ...utils, onSelectMoment, onSeek };
}

describe('MatchTimeline', () => {
  it('draws icons for goals, cards and substitutions, not for corners or throw-ins', () => {
    renderTimeline({
      moments: [
        moment(1, 'Goal', 67),
        moment(2, 'YellowCard', 30, 'Away'),
        moment(3, 'Substitution', 60),
        moment(4, 'Corner', 20),
        moment(5, 'ThrowIn', 21),
      ],
    });

    expect(screen.getAllByTestId('timeline-icon')).toHaveLength(3);
    expect(screen.getByRole('button', { name: "Goal, 68', North Bank" })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: "Yellow card, 31', Harbour Town" })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: "Substitution, 61', North Bank" })).toBeInTheDocument();
  });

  it('offsets icons that sit at the same minute so they do not overlap', () => {
    renderTimeline({ moments: [moment(1, 'Goal', 50), moment(2, 'YellowCard', 50)] });

    const bottoms = screen.getAllByTestId('timeline-icon').map((i) => i.style.bottom);
    expect(new Set(bottoms).size).toBe(2);
  });

  it('keeps icons a full icon width apart on a narrow track', () => {
    const moments = [moment(1, 'Goal', 20), moment(2, 'Goal', 25)];
    const lanesOf = () => screen.getAllByTestId('timeline-icon').map((i) => i.style.bottom);

    const wide = renderTimeline({ moments });
    expect(new Set(lanesOf()).size).toBe(1);
    wide.unmount();

    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(340);
    renderTimeline({ moments });
    expect(new Set(lanesOf()).size).toBe(2);
  });

  it('calls onSelectMoment with the moment id when an icon is clicked', () => {
    const { onSelectMoment } = renderTimeline({ moments: [moment(7, 'Goal', 12)] });

    fireEvent.click(screen.getByRole('button', { name: "Goal, 13', North Bank" }));

    expect(onSelectMoment).toHaveBeenCalledWith('m:7');
  });

  it('seeks to the minute chosen with the arrow keys, committing on Enter', () => {
    const { onSeek } = renderTimeline({ clock: clockOf(67) });
    const slider = screen.getByRole('slider');

    fireEvent.keyDown(slider, { key: 'ArrowLeft' });
    fireEvent.keyDown(slider, { key: 'ArrowLeft' });
    expect(screen.getByTestId('timeline-readout')).toHaveTextContent("66'");
    expect(onSeek).not.toHaveBeenCalled();
    fireEvent.keyDown(slider, { key: 'Enter' });

    expect(onSeek).toHaveBeenCalledTimes(1);
    expect(onSeek).toHaveBeenCalledWith({ period: 'SecondHalf', minute: 65 });
  });

  it('discards a half-made choice on blur without seeking', () => {
    const { onSeek } = renderTimeline();
    const slider = screen.getByRole('slider');

    fireEvent.keyDown(slider, { key: 'ArrowLeft' });
    expect(screen.getByTestId('timeline-readout')).toBeInTheDocument();
    fireEvent.blur(slider);

    expect(onSeek).not.toHaveBeenCalled();
    expect(screen.queryByTestId('timeline-readout')).not.toBeInTheDocument();
  });

  it('seeks into the first half', () => {
    const { onSeek } = renderTimeline({ clock: clockOf(30) });
    const slider = screen.getByRole('slider');

    fireEvent.keyDown(slider, { key: 'Home' });
    fireEvent.keyDown(slider, { key: 'ArrowRight' });
    fireEvent.keyDown(slider, { key: 'Enter' });

    expect(onSeek).toHaveBeenCalledWith({ period: 'FirstHalf', minute: 1 });
  });

  it('cannot go past the live clock', () => {
    const { onSeek } = renderTimeline({ clock: clockOf(67) });
    const slider = screen.getByRole('slider');

    expect(slider).toHaveAttribute('max', '67');
    fireEvent.keyDown(slider, { key: 'ArrowRight' });
    fireEvent.keyDown(slider, { key: 'ArrowRight' });
    fireEvent.keyDown(slider, { key: 'Enter' });

    expect(onSeek).toHaveBeenCalledWith({ period: 'SecondHalf', minute: 67 });
  });

  it('accounts for first-half added time on the slider', () => {
    const added = { ...moment(1, 'Goal', 46), clock: { period: 'FirstHalf', minute: 46, second: 0, display: '46:00' } };
    const { onSeek } = renderTimeline({ clock: clockOf(60), moments: [added as MomentView] });
    const slider = screen.getByRole('slider');

    // Half time is pushed out to 47, so the second-half clock at 60 sits at slider position 62.
    expect(slider).toHaveAttribute('max', '62');
    fireEvent.keyDown(slider, { key: 'ArrowLeft' });
    fireEvent.keyDown(slider, { key: 'Enter' });
    expect(onSeek).toHaveBeenCalledWith({ period: 'SecondHalf', minute: 59 });
  });

  it('shows a replay playhead only when a replay is playing, and labels the live edge until full time', () => {
    const { rerender } = renderTimeline();
    expect(screen.queryByTestId('timeline-playhead')).not.toBeInTheDocument();
    expect(screen.getByText('Live')).toBeInTheDocument();

    const props = {
      detail,
      moments: [],
      clock: clockOf(67),
      fullTime: false,
      replayClock: clockOf(40),
      onSelectMoment: vi.fn(),
      onSeek: vi.fn(),
    };
    rerender(<MatchTimeline {...props} />);
    expect(screen.getByTestId('timeline-playhead')).toHaveAccessibleName("Replay at 41'");

    rerender(<MatchTimeline {...props} fullTime />);
    expect(screen.queryByText('Live')).not.toBeInTheDocument();
  });
});
