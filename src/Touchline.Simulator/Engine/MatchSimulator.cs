using System.Globalization;
using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;
using Touchline.Domain.Teams;
using static Touchline.Simulator.Engine.EngineTuning;

namespace Touchline.Simulator.Engine;

/// <summary>
/// Plays a seeded, football-realistic match as a stream of events (ADR-014). The ball moves
/// between pitch zones through passes, carries, pressure, tackles, fouls and shots, with
/// every probability set by the two teams' tendencies. The same teams and seed always
/// produce exactly the same events.
/// </summary>
public sealed class MatchSimulator
{
    private readonly TeamProfile _home;
    private readonly TeamProfile _away;
    private readonly int _seed;

    /// <summary>
    /// Sets up a match.
    /// </summary>
    /// <param name="home">The home team, attacking towards X = 105.</param>
    /// <param name="away">The away team, attacking towards X = 0.</param>
    /// <param name="seed">Random seed. The same seed replays the same match.</param>
    /// <param name="matchId">The match ID stamped on every event. Defaults to <c>{home}-{away}-{seed}</c>.</param>
    public MatchSimulator(TeamProfile home, TeamProfile away, int seed, string? matchId = null)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(away);

        _home = home;
        _away = away;
        _seed = seed;
        MatchId = matchId ?? string.Create(CultureInfo.InvariantCulture, $"{home.ShortName}-{away.ShortName}-{seed}").ToLowerInvariant();
    }

    /// <summary>The ID stamped on every event of this match.</summary>
    public string MatchId { get; }

    /// <summary>
    /// Plays the match. Events are produced lazily, one at a time, so a caller can publish
    /// each event as it's made. Each call replays the match from the start.
    /// </summary>
    /// <returns>Every event of the match, in sequence order, from kick-off to full time.</returns>
    public IEnumerable<MatchEvent> Play() => new Run(_home, _away, _seed, MatchId).Events();

    /// <summary>The mutable state of one playing of the match. Never shared.</summary>
    private sealed class Run
    {
        private readonly SeededRandom _random;
        private readonly string _matchId;
        private readonly Dictionary<TeamSide, Lineup> _lineups;

        private long _sequence;
        private int _possessionId;
        private MatchPeriod _period;
        private TimeSpan _clock;

        // The current possession.
        private TeamSide _team;
        private Player _carrier = null!;
        private PitchPosition _ball;
        private int _actionsInPossession;
        private bool _counter;
        private Player? _lastPasser;
        private int _cornersInPossession;

        public Run(TeamProfile home, TeamProfile away, int seed, string matchId)
        {
            _random = new SeededRandom(seed);
            _matchId = matchId;
            _lineups = new() { [TeamSide.Home] = new Lineup(home), [TeamSide.Away] = new Lineup(away) };
        }

        public IEnumerable<MatchEvent> Events()
        {
            foreach (var (period, start, added, kickingOff) in new[]
                     {
                         (MatchPeriod.FirstHalf, TimeSpan.Zero, FirstHalfAddedMinutes, TeamSide.Home),
                         (MatchPeriod.SecondHalf, TimeSpan.FromMinutes(45), SecondHalfAddedMinutes, TeamSide.Away)
                     })
            {
                _period = period;
                _clock = start;
                var end = start + TimeSpan.FromMinutes(45 + _random.Next(added.Min, added.Max + 1));

                foreach (var e in KickOff(kickingOff))
                {
                    yield return e;
                }

                while (_clock < end)
                {
                    foreach (var e in Step())
                    {
                        yield return e;
                    }
                }

                yield return Emit(MatchEventType.PeriodEnd, TeamSide.Home, null, Centre);
            }
        }

        private static PitchPosition Centre => new(PitchDimensions.Length / 2, PitchDimensions.Width / 2);

        private Lineup Attackers => _lineups[_team];

        private Lineup Defenders => _lineups[Other(_team)];

        private static TeamSide Other(TeamSide side) => side == TeamSide.Home ? TeamSide.Away : TeamSide.Home;

        // One action by the team in possession, with whatever it leads to.
        private IEnumerable<MatchEvent> Step()
        {
            var zone = PitchZone.Locate(_ball, _team);
            var distanceToGoal = _ball.DistanceToGoal(_team);
            var underPressure = false;

            var pressing = Defenders.Team.Tendencies.PressingIntensity;
            var pressureChance = (PressureBase + PressurePerPressingIntensity * pressing) * PressureByThird(zone.Third, pressing);
            if (_random.NextDouble() < pressureChance)
            {
                underPressure = true;
                var presser = Defenders.Pick(_random, PitchZone.Locate(_ball, Other(_team)).Third);
                yield return Emit(MatchEventType.Pressure, Other(_team), presser.Id, _ball);
                Advance(DuelSeconds);
            }

            var events = _random.NextDouble() < ShotChance(distanceToGoal)
                ? Shoot(_counter ? ShotSituation.Counter : ShotSituation.OpenPlay)
                : _random.NextDouble() < CarryShare
                    ? Carry(underPressure)
                    : Pass(underPressure);

            foreach (var e in events)
            {
                yield return e;
            }

            _actionsInPossession++;
            if (_actionsInPossession >= CounterActions)
            {
                _counter = false;
            }
        }

        // Where the defending team presses, from the attacking team's point of view. A high
        // pressing intensity pushes pressure up the pitch (the ball deep in the attacking team's
        // own third) and away from the defending team's box, where it drops into a block.
        private static double PressureByThird(Third third, double pressing) => third switch
        {
            Third.Defensive => HighPressBase + HighPressPerPressingIntensity * pressing,
            Third.Middle => 1.0,
            _ => DeepBlockBase - DeepBlockPerPressingIntensity * pressing
        };

        private double ShotChance(double distanceToGoal) =>
            distanceToGoal > ShotMaxDistance
                ? 0
                : Attackers.Team.Tendencies.ShotAppetite * ShotScale * Math.Exp(-distanceToGoal / ShotDistanceDecay);

        private IEnumerable<MatchEvent> Pass(bool underPressure)
        {
            var tendencies = Attackers.Team.Tendencies;
            var isLong = _random.NextDouble() < LongPassBase + LongPassPerDirectness * tendencies.Directness;
            var forward = isLong ? Uniform(18, 38) : Uniform(-6, 14);
            var sideways = Normal(0, 6 + 8 * tendencies.Width);
            var target = Move(_ball, forward, sideways);
            var recipient = Attackers.Pick(_random, PitchZone.Locate(target, _team).Third, except: _carrier);

            var success = PassSuccessShort
                - (isLong ? PassSuccessLongPenalty : 0)
                - (underPressure ? PassSuccessPressurePenalty : 0)
                - (PitchZone.Locate(target, _team).Third == Third.Attacking ? PassSuccessAttackingThirdPenalty : 0);
            var completed = _random.NextDouble() < success;

            yield return Emit(MatchEventType.Pass, _team, _carrier.Id, _ball, target, completed) with { RecipientPlayerId = recipient.Id };
            Advance(PassSeconds);

            if (completed)
            {
                _lastPasser = _carrier;
                _carrier = recipient;
                _ball = target;
                yield break;
            }

            var interceptor = Defenders.Pick(_random, PitchZone.Locate(target, Other(_team)).Third);
            yield return Emit(MatchEventType.Interception, Other(_team), interceptor.Id, target);
            Advance(DuelSeconds);
            ChangePossession(Other(_team), target, interceptor, wonInOpenPlay: true);
        }

        private IEnumerable<MatchEvent> Carry(bool underPressure)
        {
            var end = Move(_ball, Uniform(3, 10), Normal(0, 4));
            var pressing = Defenders.Team.Tendencies.PressingIntensity;

            var tackleChance = (TackleBase + TacklePerPressingIntensity * pressing) * (underPressure ? TackleUnderPressureFactor : 1);
            if (_random.NextDouble() >= tackleChance)
            {
                yield return Emit(MatchEventType.Carry, _team, _carrier.Id, _ball, end);
                Advance(CarrySeconds);
                _ball = end;
                yield break;
            }

            var tackler = Defenders.Pick(_random, PitchZone.Locate(_ball, Other(_team)).Third);
            var roll = _random.NextDouble();
            var inTacklersBox = PitchDimensions.IsInPenaltyArea(_ball, Other(_team));
            // A booked player eases off, which is what keeps second yellows realistic.
            var foulShare = TackleFoulShare
                * (inTacklersBox ? FoulInOwnBoxDamping : 1)
                * (Defenders.IsBooked(tackler) ? BookedPlayerFoulFactor : 1);

            if (roll < foulShare)
            {
                foreach (var e in Foul(tackler, inTacklersBox))
                {
                    yield return e;
                }

                yield break;
            }

            var won = roll < foulShare + TackleWonShare * (1 - foulShare);
            yield return Emit(MatchEventType.Tackle, Other(_team), tackler.Id, _ball, successful: won);
            Advance(DuelSeconds);

            if (won)
            {
                ChangePossession(Other(_team), _ball, tackler, wonInOpenPlay: true);
            }
        }

        private IEnumerable<MatchEvent> Foul(Player offender, bool penalty)
        {
            var fouled = _carrier;
            var defending = Other(_team);
            yield return Emit(MatchEventType.Foul, defending, offender.Id, _ball) with { FouledPlayerId = fouled.Id };
            Advance((FoulStoppageSeconds, FoulStoppageSeconds));

            // Play has stopped: the move before the foul no longer leads to an assist or a counter.
            _lastPasser = null;
            _counter = false;

            var cardRoll = _random.NextDouble();
            var card = cardRoll < StraightRedPerFoul ? CardFor(defending, offender, straightRed: true)
                : cardRoll < StraightRedPerFoul + YellowPerFoul ? CardFor(defending, offender, straightRed: false)
                : null;
            if (card is not null)
            {
                yield return card;
                Advance((CardStoppageSeconds, CardStoppageSeconds));
            }

            // The attacking team keeps the ball: a free kick, or a penalty.
            _carrier = fouled;
            if (penalty)
            {
                _carrier = Attackers.PenaltyTaker();
                _ball = PenaltySpot(_team);
                foreach (var e in Shoot(ShotSituation.Penalty))
                {
                    yield return e;
                }
            }
            else if (_ball.DistanceToGoal(_team) < FreeKickShotDistance && _random.NextDouble() < FreeKickShotShare)
            {
                _carrier = Attackers.SetPieceTaker();
                foreach (var e in Shoot(ShotSituation.SetPiece, xgFactor: FreeKickXgFactor))
                {
                    yield return e;
                }
            }
        }

        // The card a foul earns, or null for none. A second yellow is a sending-off, shown as a
        // red card. Once a team has lost MaxSentOffPerTeam players, the referee keeps the rest
        // on: a straight red becomes a yellow, and a second booking isn't given. That keeps
        // every team within realistic numbers, and nowhere near the 7-player minimum.
        private MatchEvent? CardFor(TeamSide team, Player offender, bool straightRed)
        {
            var lineup = _lineups[team];
            var canSendOff = lineup.SentOffCount < MaxSentOffPerTeam;

            if (straightRed && canSendOff)
            {
                return SendingOff(team, offender);
            }

            if (lineup.IsBooked(offender))
            {
                return canSendOff ? SendingOff(team, offender) : null;
            }

            lineup.Book(offender);
            return Emit(MatchEventType.Card, team, offender.Id, _ball) with { Card = CardColour.Yellow };
        }

        private MatchEvent SendingOff(TeamSide team, Player player)
        {
            _lineups[team].SendOff(player);
            return Emit(MatchEventType.Card, team, player.Id, _ball) with { Card = CardColour.Red };
        }

        private IEnumerable<MatchEvent> Shoot(ShotSituation situation, double xgFactor = 1)
        {
            var xg = Math.Clamp(ExpectedGoalsModel.Calculate(_ball, _team, situation) * xgFactor, 0.01, 0.95);
            var outcome = ShotOutcomeFor(xg);
            var shot = new ShotDetail(xg, outcome, situation);
            var assist = situation == ShotSituation.Penalty || _lastPasser == _carrier ? null : _lastPasser;

            yield return Emit(MatchEventType.Shot, _team, _carrier.Id, _ball, ShotEnd(outcome), shot.OnTarget) with
            {
                Shot = shot,
                AssistPlayerId = assist?.Id
            };
            Advance((ShotSeconds, ShotSeconds));

            var defending = Other(_team);
            switch (outcome)
            {
                case ShotOutcome.Goal:
                    Advance((GoalStoppageSeconds, GoalStoppageSeconds));
                    foreach (var e in KickOff(defending))
                    {
                        yield return e;
                    }

                    break;

                // A cap on corners in a row stops corner, header, save, corner chains running on.
                case ShotOutcome.Saved when _cornersInPossession < MaxCornersInPossession && _random.NextDouble() < CornerAfterSave:
                case ShotOutcome.Blocked when _cornersInPossession < MaxCornersInPossession && _random.NextDouble() < CornerAfterBlock:
                    foreach (var e in Corner())
                    {
                        yield return e;
                    }

                    break;

                case ShotOutcome.Blocked:
                    ChangePossession(defending, _ball, Defenders.Pick(_random, Third.Defensive), wonInOpenPlay: true);
                    break;

                default:
                    // Off target: a goal kick. Saved and held: the keeper's ball.
                    Advance(outcome == ShotOutcome.OffTarget
                        ? (GoalKickStoppageSeconds, GoalKickStoppageSeconds)
                        : (SaveStoppageSeconds, SaveStoppageSeconds));
                    var keeper = Defenders.OnPitch.FirstOrDefault(p => p.Position == PlayerPosition.Goalkeeper)
                        ?? Defenders.Pick(_random, Third.Defensive);
                    ChangePossession(defending, SixYardBox(defending), keeper, wonInOpenPlay: false);
                    break;
            }
        }

        private ShotOutcome ShotOutcomeFor(double xg)
        {
            if (_random.NextDouble() < xg)
            {
                return ShotOutcome.Goal;
            }

            var roll = _random.NextDouble();
            return roll < SavedShareOfMisses ? ShotOutcome.Saved
                : roll < SavedShareOfMisses + BlockedShareOfMisses ? ShotOutcome.Blocked
                : ShotOutcome.OffTarget;
        }

        private PitchPosition ShotEnd(ShotOutcome outcome)
        {
            var goal = PitchDimensions.AttackingGoal(_team);
            return outcome switch
            {
                ShotOutcome.Goal or ShotOutcome.Saved => new PitchPosition(goal.X, goal.Y + Uniform(-3.5, 3.5)),
                ShotOutcome.OffTarget => new PitchPosition(goal.X, Math.Clamp(goal.Y + Sign() * Uniform(4, 12), 0, PitchDimensions.Width)),
                _ => Move(_ball, Uniform(1, 3), Normal(0, 1))
            };
        }

        private IEnumerable<MatchEvent> Corner()
        {
            _cornersInPossession++;
            Advance((CornerStoppageSeconds, CornerStoppageSeconds));
            var goal = PitchDimensions.AttackingGoal(_team);
            var flag = new PitchPosition(goal.X, _random.Next(2) == 0 ? 0 : PitchDimensions.Width);
            var taker = Attackers.SetPieceTaker();
            var landing = new PitchPosition(
                goal.X == 0 ? Uniform(3, 11) : PitchDimensions.Length - Uniform(3, 11),
                Uniform(28, 40));
            var target = Attackers.Pick(_random, Third.Attacking, except: taker);
            var won = _random.NextDouble() < CornerDeliveryWon;

            yield return Emit(MatchEventType.Pass, _team, taker.Id, flag, landing, won) with { RecipientPlayerId = target.Id };
            Advance(PassSeconds);

            if (won)
            {
                _lastPasser = taker;
                _carrier = target;
                _ball = landing;
                foreach (var e in Shoot(ShotSituation.SetPiece))
                {
                    yield return e;
                }

                yield break;
            }

            var clearer = Defenders.Pick(_random, Third.Defensive);
            yield return Emit(MatchEventType.Interception, Other(_team), clearer.Id, landing);
            Advance(DuelSeconds);
            ChangePossession(Other(_team), landing, clearer, wonInOpenPlay: false);
        }

        private IEnumerable<MatchEvent> KickOff(TeamSide team)
        {
            ChangePossession(team, Centre, _lineups[team].Pick(_random, Third.Middle), wonInOpenPlay: false);
            yield return Emit(MatchEventType.KickOff, team, _carrier.Id, Centre);
        }

        private void ChangePossession(TeamSide team, PitchPosition at, Player player, bool wonInOpenPlay)
        {
            _team = team;
            _ball = at;
            _carrier = player;
            _possessionId++;
            _actionsInPossession = 0;
            _lastPasser = null;
            _cornersInPossession = 0;

            // Winning the ball high up the pitch, before the opponents can reorganise, is a counter.
            _counter = wonInOpenPlay && PitchZone.Locate(at, team).Third != Third.Defensive;
        }

        private MatchEvent Emit(MatchEventType type, TeamSide team, string? playerId, PitchPosition start, PitchPosition? end = null, bool successful = true) => new()
        {
            Sequence = ++_sequence,
            MatchId = _matchId,
            PossessionId = _possessionId,
            Time = new MatchTime(_period, _clock),
            Type = type,
            Team = team,
            PlayerId = playerId,
            Start = start,
            End = end,
            Successful = successful
        };

        private void Advance((int Min, int Max) seconds) =>
            _clock += TimeSpan.FromSeconds(_random.Next(seconds.Min, seconds.Max + 1));

        // Moves the ball "forward" for the team in possession, keeping it on the pitch.
        private PitchPosition Move(PitchPosition from, double forward, double sideways)
        {
            var direction = _team == TeamSide.Home ? 1 : -1;
            return new PitchPosition(
                Math.Clamp(from.X + direction * forward, 0.5, PitchDimensions.Length - 0.5),
                Math.Clamp(from.Y + sideways, 0.5, PitchDimensions.Width - 0.5));
        }

        private static PitchPosition PenaltySpot(TeamSide attacking) =>
            attacking == TeamSide.Home ? new PitchPosition(PitchDimensions.Length - 11, 34) : new PitchPosition(11, 34);

        private static PitchPosition SixYardBox(TeamSide defending) =>
            defending == TeamSide.Home ? new PitchPosition(5.5, 34) : new PitchPosition(PitchDimensions.Length - 5.5, 34);

        private double Uniform(double min, double max) => min + _random.NextDouble() * (max - min);

        private int Sign() => _random.Next(2) == 0 ? -1 : 1;

        // Box–Muller transform: two uniform samples make one normally distributed sample.
        private double Normal(double mean, double standardDeviation)
        {
            var u1 = 1.0 - _random.NextDouble();
            var u2 = _random.NextDouble();
            return mean + standardDeviation * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }
    }
}
