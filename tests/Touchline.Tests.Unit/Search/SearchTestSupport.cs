extern alias simulator;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;
using Touchline.Domain.Teams;
using Touchline.Search;

namespace Touchline.Tests.Unit.Search;

/// <summary>A seeded simulator match, and small event builders, for the indexer tests.</summary>
internal static class SearchTestSupport
{
    public const string MatchId = "nbk-tvu-42";

    public static List<MatchEvent> SeededMatch()
    {
        var teams = TeamProfileCatalog.LoadEmbedded();
        return [.. new MatchSimulator(teams["north-bank-fc"], teams["thames-valley-united"], 42).Play()];
    }

    public static PossessionRoster SeededRoster(IReadOnlyList<MatchEvent> events) => PossessionRoster.From(events[0].Metadata);

    public static PossessionRoster SmallRoster() => new(
        "Home FC",
        "Away FC",
        Enumerable.Range(1, 12).ToDictionary(n => $"p{n}", n => $"Player{(char)('A' + n)} Surname{(char)('A' + n)}", StringComparer.Ordinal),
        ["Home FC", "Away FC"]);

    public static MatchEvent Event(
        long sequence,
        int possession,
        MatchEventType type,
        string? player,
        TeamSide team = TeamSide.Home,
        int minute = 10,
        MatchPeriod period = MatchPeriod.FirstHalf) =>
        new MatchEvent
        {
            Sequence = sequence,
            MatchId = MatchId,
            PossessionId = possession,
            Time = MatchTime.At(period, minute, (int)sequence % 60),
            Type = type,
            Team = team,
            PlayerId = player,
            Start = new PitchPosition(50, 34),
            Restart = type == MatchEventType.BallOutOfPlay ? RestartKind.ThrowIn : null
        }
        .WithRequiredDetail() with
        {
            // A throw-in needs the ball wholly over a touchline.
            Start = type == MatchEventType.BallOutOfPlay ? new PitchPosition(50, -2) : new PitchPosition(50, 34)
        };

    public static MatchEvent Goal(long sequence, int possession, string player, TeamSide team = TeamSide.Home, int minute = 10) =>
        Event(sequence, possession, MatchEventType.Shot, player, team, minute) with
        {
            Start = team == TeamSide.Home ? new PitchPosition(95, 34) : new PitchPosition(10, 34),
            Shot = TestEvents.Shot(0.4, ShotOutcome.Goal),
            End = TestEvents.EndFor(ShotOutcome.Goal, team)
        };

    /// <summary>Counts <c>search.fallback</c> by reason, from a meter factory.</summary>
    public sealed class FallbackRecorder : IDisposable
    {
        private readonly MeterListener _listener;

        public FallbackRecorder(IMeterFactory factory)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Name == SearchMetrics.FallbackCounterName && ReferenceEquals(instrument.Meter.Scope, factory))
                    {
                        l.EnableMeasurementEvents(instrument);
                    }
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var pair in tags)
                {
                    if (pair.Key == SearchMetrics.ReasonTag)
                    {
                        var reason = pair.Value?.ToString() ?? "";
                        Measurements.Enqueue((reason, value));
                        for (var i = 0; i < value; i++)
                        {
                            Reasons.Enqueue(reason);
                        }
                    }
                }
            });
            _listener.Start();
        }

        /// <summary>One entry per fallback, so a counter increment of 5 appears five times.</summary>
        public ConcurrentQueue<string> Reasons { get; } = new();

        /// <summary>One entry per counter measurement, with its increment.</summary>
        public ConcurrentQueue<(string Reason, long Value)> Measurements { get; } = new();

        public void Dispose() => _listener.Dispose();
    }

    public static ServiceProvider Metrics() => new ServiceCollection().AddMetrics().BuildServiceProvider();
}
