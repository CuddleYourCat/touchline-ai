using System.Globalization;
using Touchline.Domain.Matches;
using Touchline.Domain.Officials;
using Touchline.Domain.Pitch;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;
using static Touchline.Simulator.Engine.EngineTuning;

namespace Touchline.Simulator.Engine;

/// <summary>
/// Plays a seeded, football-realistic match as a stream of events (ADR-014). The ball moves
/// between pitch zones through passes, carries, pressure, tackles, fouls and shots, with
/// every probability set by the two teams' tendencies, the players' traits and the referee.
/// The same teams, seed and options always produce exactly the same events.
/// </summary>
/// <remarks>
/// The engine is split across partial files: this one has the match loop, passes, carries
/// and possession; <c>MatchSimulator.Fouls.cs</c> the fouls, advantage and cards; and
/// <c>MatchSimulator.Shots.cs</c> the shots, where they go and penalties.
/// </remarks>
public sealed partial class MatchSimulator
{
    /// <summary>
    /// The kick-off time recorded in the metadata when none is given. It's fixed rather than
    /// "now", so a seed alone replays the identical stream.
    /// </summary>
    public static readonly DateTimeOffset DefaultKickOffUtc = new(2026, 10, 24, 14, 0, 0, TimeSpan.Zero);

    private readonly TeamProfile _home;
    private readonly TeamProfile _away;
    private readonly int _seed;
    private readonly RefereeProfile _referee;
    private readonly MatchMetadata _metadata;
    private readonly MatchdaySheet? _homeSheet;
    private readonly MatchdaySheet? _awaySheet;

    /// <summary>
    /// Sets up a match.
    /// </summary>
    /// <param name="home">The home team, attacking towards X = 105. The match is played at its venue.</param>
    /// <param name="away">The away team, attacking towards X = 0.</param>
    /// <param name="seed">Random seed. The same seed and options replay the same match.</param>
    /// <param name="options">Match ID, kick-off, referee, the kind of match and the team sheets. All optional.</param>
    /// <exception cref="ArgumentException">
    /// The options describe an invalid match, for example a fixture without a season, or a team
    /// sheet that breaks <see cref="MatchdaySheetRules"/>.
    /// </exception>
    public MatchSimulator(TeamProfile home, TeamProfile away, int seed, MatchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(away);
        options ??= new MatchOptions();

        // Checked against the full squad, so a sheet naming an unavailable player says so.
        _homeSheet = CheckedSheet(home, options.HomeSheet, options.Unavailable, nameof(MatchOptions.HomeSheet));
        _awaySheet = CheckedSheet(away, options.AwaySheet, options.Unavailable, nameof(MatchOptions.AwaySheet));

        _home = Available(home, options.Unavailable);
        _away = Available(away, options.Unavailable);
        _seed = seed;
        _referee = options.Referee ?? RefereeCatalog.ForSeed(seed);
        MatchId = MatchIdFor(_home, _away, seed, options);

        // Built here rather than when play starts, so invalid options fail at once.
        _metadata = new MatchMetadata(
            Sheet(_home, _homeSheet),
            Sheet(_away, _awaySheet),
            _home.Venue,
            options.KickOffUtc ?? DefaultKickOffUtc,
            seed,
            new RefereeSheet(_referee.Id, _referee.Name),
            options.Kind,
            options.Season,
            options.Round,
            options.ReplayOf);
    }

    /// <summary>The ID stamped on every event of this match.</summary>
    public string MatchId { get; }

    /// <summary>
    /// Plays the match. Events are produced lazily, one at a time, so a caller can publish
    /// each event as it's made. Each call replays the match from the start.
    /// </summary>
    /// <returns>
    /// Every event of the match, in sequence order: the match metadata, then kick-off to full time.
    /// </returns>
    public IEnumerable<MatchEvent> Play() => new Run(_home, _away, _homeSheet, _awaySheet, _seed, MatchId, _referee, _metadata).Events();

    // A squad without its unavailable players. The engine picks the eleven from whoever is left.
    private static TeamProfile Available(TeamProfile team, IReadOnlySet<string> unavailable)
    {
        var squad = team.Squad.Where(p => !unavailable.Contains(p.Id)).ToList();
        return squad.Count >= 11
            ? team with { Squad = squad }
            : throw new ArgumentException($"{team.Name} has only {squad.Count} players available, and a team needs 11.", nameof(unavailable));
    }

    // A chosen sheet is checked here, defensively: the Match Centre checks it too, but the
    // engine must never play an eleven it can't stand behind (ADR-050). The rules are the
    // shared MatchdaySheetRules, so the two checks can't drift apart.
    private static MatchdaySheet? CheckedSheet(TeamProfile team, MatchdaySheet? sheet, IReadOnlySet<string> unavailable, string option)
    {
        if (sheet is null)
        {
            return null;
        }

        // GroupBy rather than ToDictionary, so a squad with a repeated ID can't throw here.
        var squad = team.Squad.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Position);
        var problems = MatchdaySheetRules.Problems(sheet, squad, unavailable);
        return problems.Count == 0
            ? sheet
            : throw new ArgumentException($"{team.Name}'s team sheet is invalid: {string.Join(" ", problems)}", option);
    }

    private static TeamSheet Sheet(TeamProfile team, MatchdaySheet? sheet) =>
        new(team.Id, team.Name, team.ShortName, Lineup.StartingEleven(team, sheet).Select(p => p.Id).ToList(), Lineup.Bench(team, sheet).Select(p => p.Id).ToList());

    // A replay must never share its fixture's ID, or its stored documents would overwrite the
    // fixture's (ADR-021). By default it's named after the fixture and its own seed.
    private static string MatchIdFor(TeamProfile home, TeamProfile away, int seed, MatchOptions options)
    {
        if (options.MatchId is { } chosen)
        {
            return options.Kind == MatchKind.Replay && chosen == options.ReplayOf
                ? throw new ArgumentException($"A replay needs its own match ID, not '{chosen}', the fixture it replays.", nameof(options))
                : chosen;
        }

        // A fixture's ID names its season and round, so an exhibition with the same seed can never
        // overwrite it (or make it look like a redelivery) in the matches container (ADR-025).
        return options switch
        {
            { Kind: MatchKind.Replay, ReplayOf: { } original } => string.Create(CultureInfo.InvariantCulture, $"{original}-replay-{seed}"),
            { Kind: MatchKind.Fixture, Season: { } season, Round: { } round } =>
                string.Create(CultureInfo.InvariantCulture, $"{home.ShortName}-{away.ShortName}-{season}-r{round}-{seed}").ToLowerInvariant(),
            _ => string.Create(CultureInfo.InvariantCulture, $"{home.ShortName}-{away.ShortName}-{seed}").ToLowerInvariant()
        };
    }

    /// <summary>The mutable state of one playing of the match. Never shared.</summary>
    private sealed partial class Run
    {
        private readonly SeededRandom _random;
        private readonly string _matchId;
        private readonly RefereeProfile _referee;
        private readonly MatchMetadata _metadata;
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

        // A completed high pass into the box: the next action can be a header (ADR-020).
        private bool _crossArrived;

        public Run(TeamProfile home, TeamProfile away, MatchdaySheet? homeSheet, MatchdaySheet? awaySheet, int seed, string matchId, RefereeProfile referee, MatchMetadata metadata)
        {
            _random = new SeededRandom(seed);
            _matchId = matchId;
            _referee = referee;
            _metadata = metadata;
            _lineups = new() { [TeamSide.Home] = new Lineup(home, homeSheet), [TeamSide.Away] = new Lineup(away, awaySheet) };
            _substitutionRandom = new SeededRandom(seed ^ _substitutionStream);
            _plans = new() { [TeamSide.Home] = PlanFor(_lineups[TeamSide.Home]), [TeamSide.Away] = PlanFor(_lineups[TeamSide.Away]) };
        }

        public IEnumerable<MatchEvent> Events()
        {
            _period = MatchPeriod.FirstHalf;

            // The first event (ADR-019). Team and Start are required members, so they take the
            // conventional values: the home side and the centre spot.
            yield return Emit(MatchEventType.MatchMetadata, TeamSide.Home, null, Centre) with { Metadata = _metadata };

            foreach (var (period, start, added, kickingOff) in new[]
                     {
                         (MatchPeriod.FirstHalf, TimeSpan.Zero, FirstHalfAddedMinutes, TeamSide.Home),
                         (MatchPeriod.SecondHalf, TimeSpan.FromMinutes(45), SecondHalfAddedMinutes, TeamSide.Away)
                     })
            {
                _period = period;
                _clock = start;
                var end = start + TimeSpan.FromMinutes(45 + _random.Next(added.Min, added.Max + 1));

                // Half time is a stoppage, and a change made there doesn't use a window (ADR-064).
                if (period == MatchPeriod.SecondHalf)
                {
                    foreach (var e in HalfTimeSubstitutions())
                    {
                        yield return e;
                    }
                }

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

                // The whistle for the end of the half is a stoppage too.
                foreach (var e in ShowPendingCards())
                {
                    yield return e;
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

            // A cross can only be met by the very next action.
            var headerChance = _crossArrived ? CrossHeaderShare : 0;
            _crossArrived = false;

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
                ? Shoot(_counter ? ShotSituation.Counter : ShotSituation.OpenPlay, headerChance: headerChance, underPressure: underPressure)
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
                : Attackers.Team.Tendencies.ShotAppetite * ShotScale * PortableMath.Exp(-distanceToGoal / ShotDistanceDecay);

        private IEnumerable<MatchEvent> Pass(bool underPressure)
        {
            var tendencies = Attackers.Team.Tendencies;
            var isLong = _random.NextDouble() < LongPassBase + LongPassPerDirectness * tendencies.Directness;
            var forward = isLong ? Uniform(18, 38) : Uniform(-6, 14);
            var sideways = Normal(0, 6 + 8 * tendencies.Width);
            var target = Move(_ball, forward, sideways);
            var recipient = Attackers.Pick(_random, PitchZone.Locate(target, _team).Third, except: _carrier);
            var intoBox = PitchDimensions.IsInPenaltyArea(target, Other(_team));
            var height = PassHeightFor(isLong, isCross: intoBox && IsWideInAttack(_ball));

            // xPass: how often an average player completes this pass (ADR-019), including its height (ADR-020).
            var expectedCompletion = PassSuccessShort
                - (isLong ? PassSuccessLongPenalty : 0)
                - (underPressure ? PassSuccessPressurePenalty : 0)
                - (PitchZone.Locate(target, _team).Third == Third.Attacking ? PassSuccessAttackingThirdPenalty : 0)
                - height switch { PassHeight.High => PassSuccessHighPenalty, PassHeight.Low => PassSuccessLowPenalty, _ => 0 };
            var weakFoot = UsesWeakFoot(_carrier, allowWeakFoot: true);
            var footFactor = FootFactor(_carrier, weakFootPossible: true, weakFoot, WeakFootPassFactor, StrongFootPassFactor);
            var completed = _random.NextDouble() < PassCompletion(_carrier, expectedCompletion, footFactor);
            var speed = Math.Round(Uniform(isLong ? LongPassSpeedKmh : ShortPassSpeedKmh), 1);

            // A pass that goes astray can run out of play (ADR-022): a long ball through to the
            // keeper's line for a goal kick, anything else over the nearer touchline.
            (PitchPosition At, RestartKind Restart)? outOfPlay = completed || _random.NextDouble() >= FailedPassOutShare ? null
                : isLong && PitchZone.Locate(target, _team).Third == Third.Attacking && _random.NextDouble() < LongBallOverGoalLineShare
                    ? (AtGoalLine(Uniform(0.5, 3), target.Y), RestartKind.GoalKick)
                    : (OverTheTouchline(target), RestartKind.ThrowIn);

            var pass = Emit(MatchEventType.Pass, _team, _carrier.Id, _ball, outOfPlay?.At ?? target, completed) with
            {
                RecipientPlayerId = recipient.Id,
                UnderPressure = underPressure,
                Pass = new PassDetail(expectedCompletion, speed, weakFoot, height)
            };
            yield return pass;
            Advance(PassSeconds);

            if (completed)
            {
                _lastPasser = _carrier;
                _carrier = recipient;
                _ball = target;
                _crossArrived = height == PassHeight.High && intoBox;
                yield break;
            }

            if (outOfPlay is var (crossed, restart))
            {
                foreach (var e in OutOffAPass(crossed, restart))
                {
                    yield return e;
                }

                yield break;
            }

            var interceptor = Defenders.Pick(_random, PitchZone.Locate(target, Other(_team)).Third);
            yield return Emit(MatchEventType.Interception, Other(_team), interceptor.Id, target);
            Advance(DuelSeconds);

            // A cross cut out in the box is often headed behind for a corner.
            if (height == PassHeight.High && intoBox && _cornersInPossession < MaxCornersInPossession && _random.NextDouble() < CrossOutForCornerShare)
            {
                foreach (var e in BallOut(Other(_team), interceptor, AtGoalLine(Uniform(0.2, 2), target.Y), RestartKind.Corner))
                {
                    yield return e;
                }

                foreach (var e in Corner())
                {
                    yield return e;
                }

                yield break;
            }

            ChangePossession(Other(_team), target, interceptor, wonInOpenPlay: true);
        }

        // The passer's team put it out: a goal kick or a throw-in to the opponents.
        private IEnumerable<MatchEvent> OutOffAPass(PitchPosition crossed, RestartKind restart)
        {
            var passingTeam = _team;
            foreach (var e in BallOut(passingTeam, _carrier, crossed, restart))
            {
                yield return e;
            }

            var defending = Other(passingTeam);
            var restartPlay = restart == RestartKind.GoalKick ? GoalKick(defending) : ThrowIn(defending, crossed);
            foreach (var e in restartPlay)
            {
                yield return e;
            }
        }

        // A goal kick (ADR-022): the defending team's keeper restarts at once from the six-yard
        // box, short or long, as an ordinary pass. An iterator, like every restart, so nothing
        // happens until it's enumerated, after the ball has gone out.
        private IEnumerable<MatchEvent> GoalKick(TeamSide team)
        {
            var lineup = _lineups[team];
            ChangePossession(team, SixYardBox(team), lineup.Goalkeeper ?? lineup.Pick(_random, Third.Defensive), wonInOpenPlay: false);
            foreach (var e in Pass(underPressure: false))
            {
                yield return e;
            }
        }

        // The nearer touchline to a point, wholly over it, at the same distance along the pitch.
        private PitchPosition OverTheTouchline(PitchPosition near)
        {
            var beyond = PitchDimensions.BallRadius + Uniform(0.2, 2);
            var y = near.Y < PitchDimensions.Width / 2 ? -beyond : PitchDimensions.Width + beyond;
            return new PitchPosition(Math.Clamp(near.X, 1, PitchDimensions.Length - 1), y);
        }

        // The ball goes out (ADR-022): the event where it crossed, the stoppage, any cards the
        // referee owes and any substitutions due, because a dead ball is a stoppage. The restart
        // is the caller's.
        private IEnumerable<MatchEvent> BallOut(TeamSide lastTouch, Player lastPlayer, PitchPosition crossed, RestartKind restart)
        {
            yield return Emit(MatchEventType.BallOutOfPlay, lastTouch, lastPlayer.Id, crossed) with { Restart = restart };
            Advance(restart switch
            {
                RestartKind.ThrowIn => ThrowInStoppageSeconds,
                RestartKind.GoalKick => (GoalKickStoppageSeconds, GoalKickStoppageSeconds),
                _ => (CornerStoppageSeconds, CornerStoppageSeconds)
            });

            foreach (var e in Stoppage())
            {
                yield return e;
            }
        }

        // A throw-in from where the ball crossed the touchline: a short, low ball by hand,
        // recorded as a pass, as data feeds do.
        private IEnumerable<MatchEvent> ThrowIn(TeamSide team, PitchPosition crossed)
        {
            var spot = new PitchPosition(crossed.X, crossed.Y < 0 ? 0 : PitchDimensions.Width);
            ChangePossession(team, spot, _lineups[team].Pick(_random, PitchZone.Locate(spot, team).Third), wonInOpenPlay: false);

            var infield = spot.Y == 0 ? 1 : -1;
            var target = Move(spot, Uniform(-4, 14), infield * Uniform(4, 14));
            var recipient = Attackers.Pick(_random, PitchZone.Locate(target, _team).Third, except: _carrier);
            // xPass is the average thrower's; the thrower's own passing changes the result, as on every pass.
            var completed = _random.NextDouble() < PassCompletion(_carrier, ThrowInCompletion, footFactor: 1);

            yield return Emit(MatchEventType.Pass, _team, _carrier.Id, spot, target, completed) with
            {
                RecipientPlayerId = recipient.Id,
                Pass = new PassDetail(ThrowInCompletion, Math.Round(Uniform(ThrowInSpeedKmh), 1), weakFoot: false, PassHeight.Low)
            };
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

        private bool IsWideInAttack(PitchPosition at)
        {
            var zone = PitchZone.Locate(at, _team);
            return zone.Third == Third.Attacking && zone.Channel != Channel.Centre;
        }

        private PassHeight PassHeightFor(bool isLong, bool isCross)
        {
            if (isCross)
            {
                return _random.NextDouble() < CrossHighShare ? PassHeight.High : PassHeight.Low;
            }

            var (ground, low) = isLong ? LongPassHeights : ShortPassHeights;
            var roll = _random.NextDouble();
            return roll < ground ? PassHeight.Ground : roll < ground + low ? PassHeight.Low : PassHeight.High;
        }

        private IEnumerable<MatchEvent> Carry(bool underPressure)
        {
            var end = Move(_ball, Uniform(3, 10), Normal(0, 4));
            var pressing = Defenders.Team.Tendencies.PressingIntensity;

            var tackleChance = (TackleBase + TacklePerPressingIntensity * pressing) * (underPressure ? TackleUnderPressureFactor : 1);
            if (_random.NextDouble() >= tackleChance)
            {
                yield return Emit(MatchEventType.Carry, _team, _carrier.Id, _ball, end) with
                {
                    UnderPressure = underPressure,
                    Carry = new CarryDetail(CarrySpeed(_carrier))
                };
                Advance(CarrySeconds);
                _ball = end;
                yield break;
            }

            var tackler = Defenders.Pick(_random, PitchZone.Locate(_ball, Other(_team)).Third);
            var roll = _random.NextDouble();
            var inTacklersBox = PitchDimensions.IsInPenaltyArea(_ball, Other(_team));
            var foulShare = FoulShare(tackler, inTacklersBox);

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

            if (!won)
            {
                yield break;
            }

            // A tackle that wins the ball can knock it out off the tackler: the attacking team
            // keeps it with a throw-in (ADR-022).
            if (_random.NextDouble() < TackleOutShare)
            {
                var attacking = _team;
                var crossed = OverTheTouchline(_ball);
                foreach (var e in BallOut(Other(attacking), tackler, crossed, RestartKind.ThrowIn))
                {
                    yield return e;
                }

                foreach (var e in ThrowIn(attacking, crossed))
                {
                    yield return e;
                }

                yield break;
            }

            ChangePossession(Other(_team), _ball, tackler, wonInOpenPlay: true);
        }

        // The corner kick itself. The ball has already gone out (BallOut), which took the
        // stoppage and showed any cards owed.
        private IEnumerable<MatchEvent> Corner()
        {
            _cornersInPossession++;
            var goal = PitchDimensions.AttackingGoal(_team);
            var flag = new PitchPosition(goal.X, _random.Next(2) == 0 ? 0 : PitchDimensions.Width);
            var taker = Attackers.SetPieceTaker();
            var landing = new PitchPosition(
                goal.X == 0 ? Uniform(3, 11) : PitchDimensions.Length - Uniform(3, 11),
                Uniform(28, 40));
            var target = Attackers.Pick(_random, Third.Attacking, except: taker);
            var won = _random.NextDouble() < PassCompletion(taker, CornerDeliveryWon, footFactor: 1);
            var speed = Math.Round(Uniform(LongPassSpeedKmh), 1);

            yield return Emit(MatchEventType.Pass, _team, taker.Id, flag, landing, won) with
            {
                RecipientPlayerId = target.Id,
                Pass = new PassDetail(CornerDeliveryWon, speed, weakFoot: false, PassHeight.High)
            };
            Advance(PassSeconds);

            if (won)
            {
                _lastPasser = taker;
                _carrier = target;
                _ball = landing;
                foreach (var e in Shoot(ShotSituation.SetPiece, headerChance: CornerHeaderShare))
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
            _crossArrived = false;

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

        private static PitchPosition SixYardBox(TeamSide defending) =>
            defending == TeamSide.Home ? new PitchPosition(5.5, 34) : new PitchPosition(PitchDimensions.Length - 5.5, 34);

        private double Uniform(double min, double max) => min + _random.NextDouble() * (max - min);

        private double Uniform((double Min, double Max) range) => Uniform(range.Min, range.Max);

        private int Sign() => _random.Next(2) == 0 ? -1 : 1;

        // Picks an index with probability proportional to its weight. Weights needn't add up to 1.
        private int PickWeighted(IReadOnlyList<double> weights)
        {
            var roll = _random.NextDouble() * weights.Sum();
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

        // Box–Muller transform: two uniform samples make one normally distributed sample.
        private double Normal(double mean, double standardDeviation)
        {
            var u1 = 1.0 - _random.NextDouble();
            var u2 = _random.NextDouble();
            return mean + standardDeviation * Math.Sqrt(-2 * PortableMath.Log(u1)) * PortableMath.Cos(2 * Math.PI * u2);
        }
    }
}
