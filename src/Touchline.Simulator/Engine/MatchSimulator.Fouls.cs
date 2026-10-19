using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;
using Touchline.Domain.Teams;
using static Touchline.Simulator.Engine.EngineTuning;

namespace Touchline.Simulator.Engine;

public sealed partial class MatchSimulator
{
    private sealed partial class Run
    {
        // Cards the referee owes from fouls where advantage was played, shown at the next stoppage (ADR-020).
        private readonly List<PendingCard> _pendingCards = [];

        // How likely a tackle is to be given as a foul: the tackler's temperament, how readily
        // the referee blows, and the restraint of a player already booked or in their own box.
        private double FoulShare(Player tackler, bool inTacklersBox)
        {
            var share = TackleFoulShare
                * (inTacklersBox ? FoulInOwnBoxDamping : 1)
                * (Defenders.IsBooked(tackler) ? BookedPlayerFoulFactor : 1)
                * (WhistleFactorMin + WhistleFactorRange * _referee.Whistle)
                * DisciplineFactor(tackler);

            return Math.Min(MaxFoulShare, share);
        }

        // 1.41 for a hot-head (0), 1 for an average player (0.5), 0.59 for the calmest (1).
        private static double DisciplineFactor(Player player) => 1 + DisciplineFactorSlope * (0.5 - player.Discipline);

        private IEnumerable<MatchEvent> Foul(Player offender, bool penalty)
        {
            var fouled = _carrier;
            var defending = Other(_team);

            // The card is decided now, from the foul, whenever it's shown.
            var cardFactor = (StrictnessFactorMin + StrictnessFactorRange * _referee.Strictness) * DisciplineFactor(offender);
            var cardRoll = _random.NextDouble();
            bool? straightRed = cardRoll < StraightRedPerFoul * cardFactor ? true
                : cardRoll < (StraightRedPerFoul + YellowPerFoul) * cardFactor ? false
                : null;

            // Advantage: never for a penalty, and only once the fouled team is out of its own third.
            var advantage = !penalty
                && PitchZone.Locate(_ball, _team).Third != Third.Defensive
                && _random.NextDouble() < AdvantageShareMin + AdvantageShareRange * _referee.Advantage;

            var foul = Emit(MatchEventType.Foul, defending, offender.Id, _ball) with
            {
                FouledPlayerId = fouled.Id,
                AdvantagePlayed = advantage
            };
            yield return foul;

            if (advantage)
            {
                // Play goes on: the fouled player keeps the ball, and the move, its counter and
                // its assist all carry on. Any card waits for the next stoppage.
                if (straightRed is { } red)
                {
                    _pendingCards.Add(new PendingCard(defending, offender, red, foul.Sequence));
                }

                Advance(DuelSeconds);
                yield break;
            }

            Advance((FoulStoppageSeconds, FoulStoppageSeconds));

            // Play has stopped: the move before the foul no longer leads to an assist or a counter.
            _lastPasser = null;
            _counter = false;
            _crossArrived = false;

            // Cards owed from earlier advantages come first, then this foul's own.
            if (straightRed is { } isRed)
            {
                _pendingCards.Add(new PendingCard(defending, offender, isRed, foul.Sequence));
            }

            foreach (var e in ShowPendingCards())
            {
                yield return e;
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

        // Shows every card owed, at a stoppage, in the order the fouls happened. A player sent
        // off in the meantime has already gone, so a later card for them is dropped.
        private IEnumerable<MatchEvent> ShowPendingCards()
        {
            if (_pendingCards.Count == 0)
            {
                yield break;
            }

            var owed = _pendingCards.ToList();
            _pendingCards.Clear();

            foreach (var pending in owed)
            {
                if (!_lineups[pending.Team].IsOnPitch(pending.Offender))
                {
                    continue;
                }

                var card = CardFor(pending.Team, pending.Offender, pending.StraightRed, pending.FoulSequence);
                if (card is not null)
                {
                    yield return card;
                    Advance((CardStoppageSeconds, CardStoppageSeconds));
                }
            }
        }

        // The card a foul earns, or null for none. A second yellow is a sending-off, shown as a
        // red card. Once a team has lost MaxSentOffPerTeam players, the referee keeps the rest
        // on: a straight red becomes a yellow, and a second booking isn't given. That keeps
        // every team within realistic numbers, and nowhere near the 7-player minimum.
        private MatchEvent? CardFor(TeamSide team, Player offender, bool straightRed, long foulSequence)
        {
            var lineup = _lineups[team];
            var canSendOff = lineup.SentOffCount < MaxSentOffPerTeam;

            if (straightRed && canSendOff)
            {
                return SendingOff(team, offender, foulSequence);
            }

            if (lineup.IsBooked(offender))
            {
                return canSendOff ? SendingOff(team, offender, foulSequence) : null;
            }

            lineup.Book(offender);
            return Emit(MatchEventType.Card, team, offender.Id, _ball) with { Card = CardColour.Yellow, FoulSequence = foulSequence };
        }

        private MatchEvent SendingOff(TeamSide team, Player player, long foulSequence)
        {
            _lineups[team].SendOff(player);
            return Emit(MatchEventType.Card, team, player.Id, _ball) with { Card = CardColour.Red, FoulSequence = foulSequence };
        }

        private static PitchPosition PenaltySpot(TeamSide attacking) =>
            attacking == TeamSide.Home ? new PitchPosition(PitchDimensions.Length - 11, 34) : new PitchPosition(11, 34);

        private readonly record struct PendingCard(TeamSide Team, Player Offender, bool StraightRed, long FoulSequence);
    }
}
