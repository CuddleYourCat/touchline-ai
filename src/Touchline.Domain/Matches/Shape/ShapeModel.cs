using Touchline.Domain.Pitch;
using Touchline.Domain.Teams;

namespace Touchline.Domain.Matches.Shape;

/// <summary>One team as the shape model sees it: who starts, where they play, and how the team likes to play.</summary>
/// <remarks>
/// The formation is worked out once per instance, on first use, and reused by every
/// <see cref="ShapeModel.Place"/> call. A copy made with <c>with</c> works it out afresh.
/// </remarks>
/// <param name="Sheet">The team sheet.</param>
/// <param name="Tendencies">How the team likes to play.</param>
/// <param name="Positions">Each player's profile position, keyed by player ID. A player missing from it plays in midfield.</param>
public sealed record ShapeTeam(TeamSheet Sheet, TeamTendencies Tendencies, IReadOnlyDictionary<string, PlayerPosition> Positions)
{
    // A 4-4-2 read from sheet order, for a team with no profile.
    private static readonly PlayerPosition[] _defaultLines =
    [
        PlayerPosition.Goalkeeper,
        PlayerPosition.Defender, PlayerPosition.Defender, PlayerPosition.Defender, PlayerPosition.Defender,
        PlayerPosition.Midfielder, PlayerPosition.Midfielder, PlayerPosition.Midfielder, PlayerPosition.Midfielder,
        PlayerPosition.Forward, PlayerPosition.Forward
    ];

    private IReadOnlyList<Slot>? _slots;

    /// <summary>A team from its sheet and profile.</summary>
    /// <param name="sheet">The team sheet.</param>
    /// <param name="profile">The team profile, for each player's position and the team's tendencies.</param>
    public ShapeTeam(TeamSheet sheet, TeamProfile profile)
        : this(sheet, profile.Tendencies, profile.Squad.ToDictionary(p => p.Id, p => p.Position, StringComparer.Ordinal))
    {
    }

    // A copy must not share the cached formation, since its sheet or positions may differ.
    private ShapeTeam(ShapeTeam original)
    {
        Sheet = original.Sheet;
        Tendencies = original.Tendencies;
        Positions = original.Positions;
    }

    /// <summary>
    /// A team with no profile: average tendencies and a 4-4-2 read from sheet order (the
    /// goalkeeper, then four defenders, four midfielders and two forwards).
    /// </summary>
    /// <param name="sheet">The team sheet.</param>
    /// <returns>The team.</returns>
    public static ShapeTeam WithDefaults(TeamSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var positions = sheet.StartingEleven
            .Select((id, i) => (Id: id, Line: _defaultLines[Math.Min(i, _defaultLines.Length - 1)]))
            .ToDictionary(p => p.Id, p => p.Line, StringComparer.Ordinal);
        return new ShapeTeam(sheet, TeamTendencies.Default, positions);
    }

    /// <summary>The formation slots, worked out once.</summary>
    internal IReadOnlyList<Slot> Slots => _slots ??= Formation.Slots(this);
}

/// <summary>Where the model puts one player.</summary>
/// <param name="PlayerId">The player.</param>
/// <param name="Team">The player's team.</param>
/// <param name="Position">The modelled position, always on the pitch.</param>
public sealed record PlacedPlayer(string PlayerId, TeamSide Team, PitchPosition Position);

/// <summary>
/// Places every player on the pitch from formation slots, the ball and the phase of play
/// (ADR-035). The engine tracks only the ball, so these positions are <b>modelled</b>, not
/// tracked: plausible enough to draw, never good enough for offside or pressing distances.
/// </summary>
/// <remarks>
/// It's a pure function with no randomness, so the same input always gives exactly the same
/// shape. Substitutions aren't modelled, because the engine has none yet: the starting
/// eleven play the whole match, less anyone sent off. Each team works in its own frame (its goal line at 0, attacking towards
/// <see cref="PitchDimensions.Length"/>), and the away team is rotated half a turn onto the
/// pitch. The home team attacks left to right in both halves (see <see cref="PitchDimensions"/>),
/// so nothing swaps at half time.
/// </remarks>
public static class ShapeModel
{
    private const double _centreX = PitchDimensions.Length / 2;
    private const double _centreY = PitchDimensions.Width / 2;
    private const double _edge = 1;

    // Depths of each line from the team's own goal line, in metres, before any shift.
    private const double _goalkeeperDepth = 5;
    private const double _defenceDepth = 24;
    private const double _midfieldDepth = 40;
    private const double _attackDepth = 55;
    private const double _outfieldMinDepth = 4;

    // How far the block follows the ball along the length, and how far the phase moves it.
    private const double _followBall = 0.45;
    private const double _inPossessionPush = 10;
    private const double _defendingDrop = -4;
    private const double _maxPressPush = 8;

    // How far apart the lines are, relative to the defence, in and out of possession.
    private const double _attackingDepthScale = 1.15;
    private const double _defendingDepthScale = 0.8;

    // The widest any line spans, touchline to touchline, before the width scale.
    private const double _lineSpan = 48;
    private const double _slideTowardsBall = 0.3;

    // Players near the ball are drawn to it, fading with distance.
    private const double _ballPull = 0.3;
    private const double _ballPullRange = 15;

    /// <summary>
    /// Places all the players still on the pitch.
    /// </summary>
    /// <param name="home">The home team.</param>
    /// <param name="away">The away team.</param>
    /// <param name="ball">Where the ball is. A ball beyond a line is treated as on it.</param>
    /// <param name="inPossession">The team with the ball, or null when it's out of play or loose.</param>
    /// <param name="dismissed">Players sent off, who are left out. Null for none.</param>
    /// <returns>Every remaining player, home team first, each in team-sheet order.</returns>
    public static IReadOnlyList<PlacedPlayer> Place(
        ShapeTeam home,
        ShapeTeam away,
        PitchPosition ball,
        TeamSide? inPossession,
        IReadOnlyCollection<string>? dismissed = null)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(away);

        var onPitch = new PitchPosition(Math.Clamp(ball.X, 0, PitchDimensions.Length), Math.Clamp(ball.Y, 0, PitchDimensions.Width));
        return
        [
            .. PlaceTeam(home, TeamSide.Home, onPitch, inPossession, dismissed),
            .. PlaceTeam(away, TeamSide.Away, onPitch, inPossession, dismissed)
        ];
    }

    /// <summary>
    /// Where the ball is once an event is over, for the shape after it.
    /// </summary>
    /// <param name="matchEvent">The event.</param>
    /// <returns>Its end, or its start, or null for an event with no ball in it (the metadata and a period's end).</returns>
    public static PitchPosition? BallAfter(MatchEvent matchEvent)
    {
        ArgumentNullException.ThrowIfNull(matchEvent);
        return matchEvent.Type is MatchEventType.MatchMetadata or MatchEventType.PeriodEnd
            ? null
            : matchEvent.End ?? matchEvent.Start;
    }

    /// <summary>
    /// Which team has the ball once an event is over.
    /// </summary>
    /// <param name="matchEvent">The event.</param>
    /// <returns>The team in possession, or null when play has stopped.</returns>
    public static TeamSide? PossessionAfter(MatchEvent matchEvent)
    {
        ArgumentNullException.ThrowIfNull(matchEvent);
        var team = matchEvent.Team;
        var other = team == TeamSide.Home ? TeamSide.Away : TeamSide.Home;
        return matchEvent.Type switch
        {
            MatchEventType.KickOff => team,
            MatchEventType.Shot => matchEvent.Shot?.Outcome switch
            {
                // A save or a block gives the ball to the defending side.
                ShotOutcome.Saved or ShotOutcome.Blocked => other,

                // A goal (then the kick-off) or a miss (then a goal kick) stops play.
                ShotOutcome.Goal or ShotOutcome.OffTarget => null,

                // Only a shot with no recorded outcome is taken as kept by the shooting team.
                _ => team
            },
            MatchEventType.Pass or MatchEventType.Carry or MatchEventType.Tackle or MatchEventType.Interception =>
                matchEvent.Successful ? team : other,
            MatchEventType.Pressure => other,
            MatchEventType.Foul when matchEvent.AdvantagePlayed => other,
            _ => null
        };
    }

    private static IEnumerable<PlacedPlayer> PlaceTeam(
        ShapeTeam team, TeamSide side, PitchPosition ball, TeamSide? inPossession, IReadOnlyCollection<string>? dismissed)
    {
        var tendencies = team.Tendencies;
        var own = ToOwnFrame(ball, side);
        bool? attacking = inPossession is { } holder ? holder == side : null;

        var blockShift = ((own.X - _centreX) * _followBall) + attacking switch
        {
            true => _inPossessionPush,
            false => _defendingDrop + (_maxPressPush * tendencies.PressingIntensity),
            null => 0
        };
        var depthScale = attacking switch { true => _attackingDepthScale, false => _defendingDepthScale, null => 1 };
        var widthScale = attacking switch
        {
            true => 0.85 + (0.4 * tendencies.Width),
            false => 0.6 + (0.1 * tendencies.Width),
            null => 0.75 + (0.2 * tendencies.Width)
        };
        var slide = (own.Y - _centreY) * _slideTowardsBall;

        foreach (var slot in team.Slots)
        {
            if (dismissed?.Contains(slot.PlayerId) == true)
            {
                continue;
            }

            var at = slot.Line == PlayerPosition.Goalkeeper
                ? Goalkeeper(own)
                : Outfield(slot, own, blockShift, depthScale, widthScale, slide);
            yield return new PlacedPlayer(slot.PlayerId, side, ToPitch(at, side));
        }
    }

    private static (double X, double Y) Goalkeeper(PitchPosition ball) =>
        (_goalkeeperDepth + Math.Clamp((ball.X - _centreX) * 0.1, -3, 6), _centreY + ((ball.Y - _centreY) * 0.15));

    private static (double X, double Y) Outfield(
        Slot slot, PitchPosition ball, double blockShift, double depthScale, double widthScale, double slide)
    {
        var depth = slot.Line switch
        {
            PlayerPosition.Defender => _defenceDepth,
            PlayerPosition.Forward => _attackDepth,
            _ => _midfieldDepth
        };
        // Outfield players never retreat into their own goal.
        var x = Math.Max(_outfieldMinDepth, _defenceDepth + ((depth - _defenceDepth) * depthScale) + blockShift);

        var across = slot.Count == 1 ? 0 : ((double)slot.Index / (slot.Count - 1)) - 0.5;
        var y = _centreY + (across * _lineSpan * widthScale) + slide;

        // The nearer the ball, the more a player is drawn towards it.
        var distance = Math.Sqrt(((ball.X - x) * (ball.X - x)) + ((ball.Y - y) * (ball.Y - y)));
        var pull = _ballPull * Math.Exp(-distance / _ballPullRange);
        return (x + ((ball.X - x) * pull), y + ((ball.Y - y) * pull));
    }

    private static PitchPosition ToOwnFrame(PitchPosition at, TeamSide side) =>
        side == TeamSide.Home ? at : new PitchPosition(PitchDimensions.Length - at.X, PitchDimensions.Width - at.Y);

    private static PitchPosition ToPitch((double X, double Y) own, TeamSide side)
    {
        var x = Math.Clamp(own.X, _edge, PitchDimensions.Length - _edge);
        var y = Math.Clamp(own.Y, _edge, PitchDimensions.Width - _edge);
        return side == TeamSide.Home ? new PitchPosition(x, y) : new PitchPosition(PitchDimensions.Length - x, PitchDimensions.Width - y);
    }
}
