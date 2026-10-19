namespace Touchline.Simulator.Engine;

/// <summary>
/// A small, fixed pseudo-random number generator (xoshiro256**, seeded through SplitMix64),
/// so a seed replays the same match on every machine and every .NET version.
/// <see cref="Random"/> with a seed is only stable within one runtime implementation and
/// version, which isn't good enough for a demo video and its cited event IDs.
/// </summary>
/// <remarks>
/// xoshiro256** and SplitMix64 are public-domain algorithms by David Blackman and
/// Sebastiano Vigna. A test pins the first outputs for a known seed, so any change to this
/// class that would alter replays fails the build.
/// </remarks>
internal sealed class SeededRandom
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    public SeededRandom(int seed)
    {
        // SplitMix64 spreads one 32-bit seed across the 256 bits of state.
        var x = unchecked((ulong)seed);
        _s0 = SplitMix64(ref x);
        _s1 = SplitMix64(ref x);
        _s2 = SplitMix64(ref x);
        _s3 = SplitMix64(ref x);
    }

    /// <summary>Starts from an exact state, so tests can check the algorithm against its published reference outputs.</summary>
    internal SeededRandom(ulong s0, ulong s1, ulong s2, ulong s3) => (_s0, _s1, _s2, _s3) = (s0, s1, s2, s3);

    /// <summary>The next 64 random bits.</summary>
    public ulong NextUInt64()
    {
        var result = RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;

        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = RotateLeft(_s3, 45);

        return result;
    }

    /// <summary>A double in [0, 1), from the top 53 bits (a double's full precision).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>An integer in [0, <paramref name="maxExclusive"/>).</summary>
    public int Next(int maxExclusive) => (int)(NextDouble() * maxExclusive);

    /// <summary>An integer in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    public int Next(int minInclusive, int maxExclusive) => minInclusive + Next(maxExclusive - minInclusive);

    private static ulong RotateLeft(ulong value, int bits) => (value << bits) | (value >> (64 - bits));

    private static ulong SplitMix64(ref ulong x)
    {
        var z = x += 0x9E3779B97F4A7C15;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }
}
