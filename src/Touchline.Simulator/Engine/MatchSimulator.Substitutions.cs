using System.Diagnostics;
using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;
using Touchline.Domain.Teams;
using static Touchline.Simulator.Engine.EngineTuning;

namespace Touchline.Simulator.Engine;

public sealed partial class MatchSimulator
{
    // Substitutions (ADR-064): up to five per team in up to three stoppage windows, plus half
    // time, which doesn't use a window. The goalkeeper is never substituted.
    private sealed partial class Run
    {
        // Mixed into the seed for the substitution stream, so its draws never move a draw of play.
        private const int _substitutionStream = 0x5B5717;

        // Where a substitution happens: the halfway line, at the near touchline.
        private static readonly PitchPosition _substitutionSpot = new(PitchDimensions.Length / 2, 0);

        // Substitutions draw from their own seeded stream, not the play's, so deciding them
        // never shifts a pass or a shot. Play only changes once a different player is on.
        private readonly SeededRandom _substitutionRandom;
        private readonly Dictionary<TeamSide, SubstitutionPlan> _plans;
        private readonly Dictionary<TeamSide, int> _goals = new() { [TeamSide.Home] = 0, [TeamSide.Away] = 0 };

        // Each team's plan is drawn once, at kick-off, from its bench: whether it makes a change
        // at half time, and the minutes and sizes of its windows. The players are chosen when
        // the window opens, from the score and the bookings then.
        private SubstitutionPlan PlanFor(Lineup lineup)
        {
            var available = lineup.OnBench.Count(p => p.Position != PlayerPosition.Goalkeeper);
            if (available == 0)
            {
                return new SubstitutionPlan(0, []);
            }

            var wanted = Math.Min(Math.Min(available, MaxSubstitutes), 1 + WeightedIndex(SubstitutesUsedWeights));

            var halfTime = _substitutionRandom.NextDouble() < HalfTimeSubstitutionChance ? 1 : 0;
            var rest = wanted - halfTime;
            // One window per weight, so never more than MaxSubstitutionWindows; Stoppage relies on it.
            var windows = Math.Min(rest, 1 + WeightedIndex(SubstitutionWindowWeights));
            Debug.Assert(windows <= MaxSubstitutionWindows, "A plan has at most MaxSubstitutionWindows windows.");
            var sizes = Enumerable.Repeat(1, windows).ToArray();
            for (var extra = rest - windows; extra > 0; extra--)
            {
                sizes[_substitutionRandom.Next(windows)]++;
            }

            var minutes = Enumerable.Range(0, windows)
                .Select(_ => _substitutionRandom.Next(SubstitutionWindowMinutes.Min, SubstitutionWindowMinutes.Max + 1))
                .Order()
                .ToList();
            return new SubstitutionPlan(halfTime, [.. minutes.Select((minute, i) => new SubstitutionWindow(minute, sizes[i]))]);
        }

        // Half time: any change a team planned for the break, before the second-half kick-off.
        private IEnumerable<MatchEvent> HalfTimeSubstitutions()
        {
            foreach (var side in new[] { TeamSide.Home, TeamSide.Away })
            {
                foreach (var e in Substitutions(side, _plans[side].HalfTime))
                {
                    yield return e;
                }
            }
        }

        // A stoppage: the cards the referee owes come first (ADR-020), then any substitutions
        // due. Each team decides on its own; a window opens at the first stoppage at or after its
        // minute. Substitutions take place within the stoppage's own time.
        private IEnumerable<MatchEvent> Stoppage()
        {
            foreach (var e in ShowPendingCards())
            {
                yield return e;
            }

            foreach (var side in new[] { TeamSide.Home, TeamSide.Away })
            {
                // The plan never has more than MaxSubstitutionWindows windows (see PlanFor), so
                // each one can simply open when it's due.
                var plan = _plans[side];
                if (plan.Windows.Count == 0 || plan.Windows[0].Minute > _clock.TotalMinutes)
                {
                    continue;
                }

                var window = plan.Windows[0];
                plan.Windows.RemoveAt(0);
                foreach (var e in Substitutions(side, window.Count))
                {
                    yield return e;
                }
            }
        }

        private IEnumerable<MatchEvent> Substitutions(TeamSide side, int count)
        {
            var lineup = _lineups[side];
            for (var i = 0; i < count && lineup.SubstitutesUsed < MaxSubstitutes; i++)
            {
                if (ChooseSubstitution(side, lineup) is not var (off, on))
                {
                    yield break;
                }

                lineup.Substitute(off, on);

                // The substitute takes over whatever the departing player was doing at the restart.
                if (_carrier == off)
                {
                    _carrier = on;
                }

                if (_lastPasser == off)
                {
                    _lastPasser = on;
                }

                yield return Emit(MatchEventType.Substitution, side, off.Id, _substitutionSpot) with { ReplacementPlayerId = on.Id };
            }
        }

        // Who comes off and who goes on. Like for like by default. After TacticalSubstitutionMinute
        // a team behind may swap a defender or midfielder for a forward, and a team ahead a forward
        // for a defender or midfielder. A booked player is likelier to come off. Only outfield
        // starters still on can come off: never the goalkeeper, nobody sent off, and nobody who
        // has already come on. Null when nobody suitable is left.
        private (Player Off, Player On)? ChooseSubstitution(TeamSide side, Lineup lineup)
        {
            var outfield = lineup.OnPitch.Where(p => p.Position != PlayerPosition.Goalkeeper && !lineup.CameOn(p)).ToList();
            var bench = lineup.OnBench.Where(p => p.Position != PlayerPosition.Goalkeeper).ToList();
            if (outfield.Count == 0 || bench.Count == 0)
            {
                return null;
            }

            var margin = _goals[side] - _goals[Other(side)];
            var tactical = margin != 0
                && _clock.TotalMinutes >= TacticalSubstitutionMinute
                && _substitutionRandom.NextDouble() < TacticalSubstitutionShare;
            if (tactical)
            {
                bool Defensive(Player p) => p.Position is PlayerPosition.Defender or PlayerPosition.Midfielder;
                var (offPool, onPool) = margin < 0
                    ? (outfield.Where(Defensive).ToList(), bench.Where(p => p.Position == PlayerPosition.Forward).ToList())
                    : (outfield.Where(p => p.Position == PlayerPosition.Forward).ToList(), bench.Where(Defensive).ToList());
                if (offPool.Count > 0 && onPool.Count > 0)
                {
                    return (PickOff(lineup, offPool), onPool[_substitutionRandom.Next(onPool.Count)]);
                }
            }

            // Like for like, from the players a substitute of the same position could replace.
            var likeForLike = outfield.Where(p => bench.Any(b => b.Position == p.Position)).ToList();
            var off = PickOff(lineup, likeForLike.Count > 0 ? likeForLike : outfield);
            var same = bench.Where(p => p.Position == off.Position).ToList();
            var choices = same.Count > 0 ? same : bench;
            return (off, choices[_substitutionRandom.Next(choices.Count)]);
        }

        private Player PickOff(Lineup lineup, List<Player> candidates) =>
            candidates[WeightedIndex([.. candidates.Select(p => lineup.IsBooked(p) ? BookedSubstitutionFactor : 1.0)])];

        private int WeightedIndex(IReadOnlyList<double> weights)
        {
            var roll = _substitutionRandom.NextDouble() * weights.Sum();
            for (var i = 0; i < weights.Count; i++)
            {
                roll -= weights[i];
                if (roll < 0)
                {
                    return i;
                }
            }

            return weights.Count - 1;
        }

        private sealed class SubstitutionPlan(int halfTime, List<SubstitutionWindow> windows)
        {
            public int HalfTime { get; } = halfTime;

            public List<SubstitutionWindow> Windows { get; } = windows;
        }

        private readonly record struct SubstitutionWindow(int Minute, int Count);
    }
}
