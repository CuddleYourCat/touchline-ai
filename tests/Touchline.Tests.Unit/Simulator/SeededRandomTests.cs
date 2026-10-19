extern alias simulator;
using FluentAssertions;
using simulator::Touchline.Simulator.Engine;

namespace Touchline.Tests.Unit.Simulator;

[TestFixture]
public class SeededRandomTests
{
    [Test]
    public void It_matches_the_xoshiro256_star_star_reference_output()
    {
        // From state {1, 2, 3, 4} the reference implementation's first output is 11520.
        var random = new SeededRandom(1, 2, 3, 4);

        random.NextUInt64().Should().Be(11520UL);
    }

    [Test]
    public void A_seed_always_produces_the_same_numbers()
    {
        // Pinned. If this fails, every seeded match replays differently: the demo video,
        // cited event IDs and calibration seeds all change. Only update it on purpose.
        var random = new SeededRandom(42);

        var first = Enumerable.Range(0, 3).Select(_ => random.NextUInt64()).ToArray();

        first.Should().Equal(_pinnedSeed42);
    }

    [Test]
    public void Doubles_stay_in_zero_to_one_and_integers_in_range()
    {
        var random = new SeededRandom(7);

        var doubles = Enumerable.Range(0, 10_000).Select(_ => random.NextDouble()).ToList();
        var ints = Enumerable.Range(0, 10_000).Select(_ => random.Next(3, 9)).ToList();

        doubles.Should().OnlyContain(d => d >= 0 && d < 1);
        doubles.Average().Should().BeApproximately(0.5, 0.02);
        ints.Should().OnlyContain(i => i >= 3 && i < 9);
        ints.Distinct().Should().HaveCount(6);
    }

    private static readonly ulong[] _pinnedSeed42 = [1546998764402558742UL, 6990951692964543102UL, 12544586762248559009UL];
}
