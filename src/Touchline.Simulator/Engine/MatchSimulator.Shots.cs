using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;
using Touchline.Domain.Teams;
using static Touchline.Simulator.Engine.EngineTuning;

namespace Touchline.Simulator.Engine;

public sealed partial class MatchSimulator
{
    private sealed partial class Run
    {
        private IEnumerable<MatchEvent> Shoot(ShotSituation situation, double xgFactor = 1, double headerChance = 0, bool underPressure = false)
        {
            // Set pieces and penalties are always struck on the stronger foot.
            var openPlay = situation is ShotSituation.OpenPlay or ShotSituation.Counter;
            var (bodyPart, weakFoot) = headerChance > 0 && _random.NextDouble() < headerChance
                ? (BodyPart.Head, false)
                : Foot(_carrier, allowWeakFoot: openPlay);

            // xG is for an average player. The shooter's finishing and foot change the result only (ADR-019).
            var xg = Math.Clamp(ExpectedGoalsModel.Calculate(_ball, _team, situation, bodyPart) * xgFactor, 0.01, 0.95);
            var (outcome, goalMouth, hitWoodwork, keeperDive, clearedOffLine) = situation == ShotSituation.Penalty
                ? PenaltyDuel(_carrier)
                : OpenShot(xg, bodyPart, weakFoot, openPlay);
            var shot = new ShotDetail(xg, outcome, situation, bodyPart, weakFoot, ShotSpeed(situation, bodyPart, weakFoot), goalMouth, hitWoodwork, keeperDive, clearedOffLine);
            var assist = situation == ShotSituation.Penalty || _lastPasser == _carrier ? null : _lastPasser;
            // A block is by an outfield player: a keeper stopping a shot is a save.
            var blocker = outcome == ShotOutcome.Blocked ? Defenders.Pick(_random, Third.Defensive, except: Defenders.Goalkeeper) : null;
            var end = ShotEnd(shot);

            yield return Emit(MatchEventType.Shot, _team, _carrier.Id, _ball, end, shot.OnTarget) with
            {
                Shot = shot,
                AssistPlayerId = assist?.Id,
                BlockedByPlayerId = blocker?.Id,
                UnderPressure = underPressure
            };
            Advance((ShotSeconds, ShotSeconds));

            var attacking = _team;
            var defending = Other(_team);
            var keeper = Defenders.Goalkeeper ?? Defenders.Pick(_random, Third.Defensive);
            var cornerAllowed = _cornersInPossession < MaxCornersInPossession;
            switch (outcome)
            {
                case ShotOutcome.Goal:
                    _goals[attacking]++;
                    Advance((GoalStoppageSeconds, GoalStoppageSeconds));
                    foreach (var e in Stoppage())
                    {
                        yield return e;
                    }

                    foreach (var e in KickOff(defending))
                    {
                        yield return e;
                    }

                    break;

                // Off the woodwork and out: the ball comes back into play, so nothing stops.
                case ShotOutcome.OffTarget when hitWoodwork:
                    Rebound();
                    break;

                // Wide or over: out of play off the shooter, so a goal kick (ADR-022).
                case ShotOutcome.OffTarget:
                    foreach (var e in BallOut(attacking, _carrier, end, RestartKind.GoalKick))
                    {
                        yield return e;
                    }

                    foreach (var e in GoalKick(defending))
                    {
                        yield return e;
                    }

                    break;

                // Turned behind by the keeper or a defender: a corner. A cap on corners in a row
                // stops corner, header, save, corner chains running on.
                case ShotOutcome.Saved when cornerAllowed && _random.NextDouble() < CornerAfterSave:
                case ShotOutcome.Blocked when cornerAllowed && _random.NextDouble() < (clearedOffLine ? ClearedOffLineCornerShare : CornerAfterBlock):
                    foreach (var e in BallOut(defending, blocker ?? keeper, BehindTheGoalLine(), RestartKind.Corner))
                    {
                        yield return e;
                    }

                    foreach (var e in Corner())
                    {
                        yield return e;
                    }

                    break;

                case ShotOutcome.Blocked:
                    ChangePossession(defending, end, blocker!, wonInOpenPlay: true);
                    break;

                default:
                    // Saved and held: the keeper's ball.
                    Advance((SaveStoppageSeconds, SaveStoppageSeconds));
                    foreach (var e in ShowPendingCards())
                    {
                        yield return e;
                    }

                    ChangePossession(defending, SixYardBox(defending), keeper, wonInOpenPlay: false);
                    break;
            }
        }

        // An open-play or set-piece shot: whether it goes in, then where it crosses the line.
        // A small share of shots that would have gone in are cleared off the line instead (ADR-022).
        private (ShotOutcome Outcome, GoalMouthPosition? GoalMouth, bool HitWoodwork, DiveDirection? KeeperDive, bool ClearedOffLine) OpenShot(
            double xg, BodyPart bodyPart, bool weakFoot, bool openPlay)
        {
            var footFactor = FootFactor(_carrier, weakFootPossible: openPlay && bodyPart != BodyPart.Head, weakFoot, WeakFootShotFactor, StrongFootShotFactor);
            // xG already counts the chance of a clearance off the line, as real models do, so a
            // goal-bound shot is drawn a little more often and some are then cleared: the
            // share that go in stays the xG.
            var scoringChance = Math.Min(MaxScoringChance, xg * FinishingFactor(_carrier) * footFactor / (1 - ClearedOffLineShareOfGoals));

            switch (ShotOutcomeFor(scoringChance))
            {
                case ShotOutcome.Goal when _random.NextDouble() < ClearedOffLineShareOfGoals:
                    return (ShotOutcome.Blocked, InsideFrame(bodyPart, saved: false), false, null, true);

                case ShotOutcome.Goal:
                    var inOffTheFrame = _random.NextDouble() < PostAndInShareOfGoals;
                    return (ShotOutcome.Goal, inOffTheFrame ? InOffTheWoodwork() : InsideFrame(bodyPart, saved: false), inOffTheFrame, null, false);

                case ShotOutcome.Saved:
                    return (ShotOutcome.Saved, InsideFrame(bodyPart, saved: true), false, null, false);

                case ShotOutcome.Blocked:
                    return (ShotOutcome.Blocked, null, false, null, false);

                default:
                    var woodwork = _random.NextDouble() < WoodworkShareOfMisses;
                    return (ShotOutcome.OffTarget, woodwork ? OnWoodwork() : Miss(bodyPart), woodwork, null, false);
            }
        }

        private ShotOutcome ShotOutcomeFor(double scoringChance)
        {
            if (_random.NextDouble() < scoringChance)
            {
                return ShotOutcome.Goal;
            }

            var roll = _random.NextDouble();
            return roll < SavedShareOfMisses ? ShotOutcome.Saved
                : roll < SavedShareOfMisses + BlockedShareOfMisses ? ShotOutcome.Blocked
                : ShotOutcome.OffTarget;
        }

        // The penalty duel (ADR-020): the keeper commits to a dive and the taker picks a sixth of
        // the goal, both from their tendencies, in the taker's frame. xG stays 0.76: the
        // tendencies decide the outcome. A miss is likelier aiming high, and less likely for a
        // good finisher. A save needs the right guess.
        private (ShotOutcome Outcome, GoalMouthPosition? GoalMouth, bool HitWoodwork, DiveDirection? KeeperDive, bool ClearedOffLine) PenaltyDuel(Player taker)
        {
            var dive = Defenders.Goalkeeper?.Dive ?? DiveBias.LeagueAverage;
            var keeperDive = (DiveDirection)PickWeighted([dive.Left, dive.Stay, dive.Right]);
            var zone = (GoalZone)PickWeighted((taker.PenaltyAim ?? PenaltyAim.LeagueAverage).Weights);
            var column = (int)zone % 3;
            var high = zone >= GoalZone.HighLeft;

            // A low shot down the middle can't go wide or hit a post: only the keeper can stop it.
            var missChance = column == 1 && !high ? 0 : (high ? PenaltyMissHigh : PenaltyMissLow) * (2 - FinishingFactor(taker));
            if (_random.NextDouble() < missChance)
            {
                var woodwork = _random.NextDouble() < PenaltyWoodworkShareOfMisses;
                return (ShotOutcome.OffTarget, woodwork ? PenaltyWoodwork(column, high) : PenaltyMiss(column, high), woodwork, keeperDive, false);
            }

            // DiveDirection and the zone's column share an order: left, middle, right.
            var guessedRight = (int)keeperDive == column;
            var saveChance = !guessedRight ? 0
                : column == 1 ? (high ? PenaltySaveCentreHigh : PenaltySaveCentreLow)
                : high ? PenaltySaveSideHigh : PenaltySaveSideLow;
            var outcome = _random.NextDouble() < saveChance ? ShotOutcome.Saved : ShotOutcome.Goal;

            return (outcome, InZone(column, high), false, keeperDive, false);
        }

        // After a shot comes back off the woodwork, either side can win the loose ball, which
        // drops between the penalty spot and the edge of the box.
        private void Rebound()
        {
            var goal = PitchDimensions.AttackingGoal(_team);
            var distance = Uniform(6, 14);
            var at = new PitchPosition(
                goal.X == 0 ? distance : PitchDimensions.Length - distance,
                Math.Clamp(PitchDimensions.Width / 2 + Normal(0, 6), 0.5, PitchDimensions.Width - 0.5));

            if (_random.NextDouble() < ReboundToAttackers)
            {
                _carrier = Attackers.Pick(_random, Third.Attacking);
                _ball = at;
                _lastPasser = null;
                _counter = false;
                return;
            }

            ChangePossession(Other(_team), at, Defenders.Pick(_random, Third.Defensive), wonInOpenPlay: true);
        }

        // Where a shot ended up (ADR-022), measured from the goal line by how far the whole ball
        // is over it: in the net for a goal (nearly always well over, rarely by under 5 cm), a
        // few metres beyond for a miss, on the line for the woodwork, just short of it for a
        // save or a clearance off the line. A block stops just in front of the shooter. The
        // shooter's right is towards Y = 0 for the home team (attacking towards X = 105) and
        // towards Y = 68 for the away team.
        private PitchPosition ShotEnd(ShotDetail shot)
        {
            if (shot.GoalMouth is not { } at)
            {
                return Move(_ball, Uniform(1, 3), Normal(0, 1));
            }

            var overBy = shot switch
            {
                { Outcome: ShotOutcome.Goal } => _random.NextDouble() < GoalLineCloseCallShareOfGoals ? Uniform(0.005, 0.05) : Uniform(0.3, 2.0),
                { ClearedOffLine: true } => _random.NextDouble() < CloseCallShareOfClearances ? -Uniform(0.005, 0.05) : -Uniform(0.1, 0.8),
                { HitWoodwork: true } => -PitchDimensions.BallRadius,
                { Outcome: ShotOutcome.Saved } => -Uniform(0.3, 1.2),
                _ => Uniform(1, 4)
            };

            var rightward = _team == TeamSide.Home ? -1 : 1;
            return AtGoalLine(overBy, Math.Clamp(PitchDimensions.Width / 2 + rightward * at.Across, -PitchDimensions.Margin, PitchDimensions.Width + PitchDimensions.Margin));
        }

        // A point whose whole ball is `overBy` metres beyond the goal line the team in
        // possession attacks (negative: short of it), at the given width.
        private PitchPosition AtGoalLine(double overBy, double y)
        {
            var beyond = PitchDimensions.BallRadius + overBy;
            return new PitchPosition(_team == TeamSide.Home ? PitchDimensions.Length + beyond : -beyond, y);
        }

        // Where a save or block turned behind for a corner crosses the goal line: wholly over,
        // a little wide of the posts.
        private PitchPosition BehindTheGoalLine() =>
            AtGoalLine(Uniform(0.2, 2), PitchDimensions.Width / 2 + Sign() * Uniform(4, 15));

        // Goal-mouth positions (ADR-020), in metres from the centre of the goal, from the
        // shooter's point of view. The ranges keep each kind clear of the others' boundaries
        // (see GoalFrame), so a goal is always inside and a miss always off the frame.

        private GoalMouthPosition InsideFrame(BodyPart bodyPart, bool saved)
        {
            // A saved shot is nearer the keeper, in the middle of the goal.
            var across = saved ? Math.Clamp(Normal(0, 1.6), -3.45, 3.45) : Uniform(-3.45, 3.45);
            var up = bodyPart == BodyPart.Head ? Uniform(0.1, 2.3)
                : _random.NextDouble() < LowShotShare ? Uniform(0.05, 1.0)
                : Uniform(1.0, 2.3);
            return GoalMouth(across, up);
        }

        private GoalMouthPosition InOffTheWoodwork() =>
            _random.NextDouble() < PostShareOfWoodwork
                ? GoalMouth(Sign() * Uniform(3.3, 3.59), Uniform(0.1, 2.3))
                : GoalMouth(Uniform(-3.4, 3.4), Uniform(2.1, 2.37));

        private GoalMouthPosition OnWoodwork() =>
            _random.NextDouble() < PostShareOfWoodwork
                ? GoalMouth(Sign() * Uniform(3.61, 3.71), Uniform(0, 2.4))
                : GoalMouth(Uniform(-3.59, 3.59), Uniform(2.39, 2.49));

        private GoalMouthPosition Miss(BodyPart bodyPart)
        {
            if (_random.NextDouble() < WideShareOfMisses)
            {
                return GoalMouth(Sign() * Uniform(3.9, 9), Uniform(0, 3.0));
            }

            // Over the bar, and occasionally skied like a rugby kick (never with the head).
            var skied = bodyPart != BodyPart.Head && _random.NextDouble() < SkiedShareOfOverMisses;
            return GoalMouth(Uniform(-5, 5), skied ? Uniform(5, 12) : Uniform(2.6, 5));
        }

        private GoalMouthPosition InZone(int column, bool high) =>
            GoalMouth(ColumnAcross(column), high ? Uniform(1.3, 2.3) : Uniform(0.05, 1.1));

        private double ColumnAcross(int column) => column switch
        {
            0 => Uniform(-3.45, -1.3),
            1 => Uniform(-1.1, 1.1),
            _ => Uniform(1.3, 3.45)
        };

        // A penalty aimed high goes over the bar; one aimed low goes wide on its side.
        private GoalMouthPosition PenaltyMiss(int column, bool high)
        {
            if (high)
            {
                return GoalMouth(ColumnAcross(column), Uniform(2.6, 4));
            }

            var side = column == 0 ? -1 : 1; // a low central penalty never misses or hits a post
            return GoalMouth(side * Uniform(3.9, 5.5), Uniform(0.05, 1.0));
        }

        // A penalty off the woodwork hits the frame where it was aimed, so its goal mouth falls
        // in the same sixth of the goal (GoalFrame.ZoneOf) and the taker's history stays true.
        // A side zone meets that side's post; a high one can meet the bar above it instead.
        private GoalMouthPosition PenaltyWoodwork(int column, bool high)
        {
            if (high && (column == 1 || _random.NextDouble() < 1 - PostShareOfWoodwork))
            {
                return GoalMouth(ColumnAcross(column), Uniform(2.39, 2.49));
            }

            var side = column == 0 ? -1 : 1; // a low central penalty never misses or hits a post
            return GoalMouth(side * Uniform(3.61, 3.71), high ? Uniform(1.3, 2.37) : Uniform(0.05, 1.1));
        }

        // Rounded to the centimetre, which keeps the stream readable. The ranges above leave
        // at least a centimetre's margin from every frame boundary, so rounding can't cross one.
        private static GoalMouthPosition GoalMouth(double across, double up) => new(Math.Round(across, 2), Math.Round(up, 2));
    }
}
