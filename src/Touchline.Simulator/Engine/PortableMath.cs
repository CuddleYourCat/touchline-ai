namespace Touchline.Simulator.Engine;

/// <summary>
/// Exp, Log and Cos that give bit-identical results on every platform.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Math.Exp"/>, <see cref="Math.Log(double)"/> and <see cref="Math.Cos"/> call the operating
/// system's C maths library, and Windows and Linux can disagree in the last bit. In a seeded
/// simulation one flipped bit can steer every later random choice, so the same seed played a
/// different match on a Windows PC and in the Linux container (ADR-001, found by CI on 22 Oct).
/// </para>
/// <para>
/// These versions use only addition, subtraction, multiplication, division and
/// <see cref="Math.Sqrt"/>, which IEEE 754 requires to be correctly rounded, so every platform
/// computes the same bits. .NET never fuses a multiply and an add on its own, which would also
/// change the rounding. They're accurate to a few units in the last place, far tighter than the
/// simulator needs; being identical everywhere is what matters.
/// </para>
/// </remarks>
internal static class PortableMath
{
    // ln 2 and π/2 split into a high part with trailing zero bits (so k × high is exact for the
    // k we see) and a low correction: Cody and Waite's range reduction.
    private const double _ln2High = 6.93147180369123816490e-01;
    private const double _ln2Low = 1.90821492927058770002e-10;
    private const double _halfPiHigh = 1.57079632673412561417e+00;
    private const double _halfPiLow = 6.07710050650619224932e-11;
    private const double _maxExp = 709.782712893384;
    private const double _minExp = -745.1332191019411;

    /// <summary>e raised to a power.</summary>
    /// <param name="x">The power.</param>
    /// <returns>eˣ.</returns>
    public static double Exp(double x)
    {
        if (double.IsNaN(x))
        {
            return double.NaN;
        }

        if (x > _maxExp)
        {
            return double.PositiveInfinity;
        }

        if (x < _minExp)
        {
            return 0;
        }

        // x = k ln 2 + r with |r| ≤ ln 2 / 2, so eˣ = 2ᵏ eʳ.
        var k = (int)Math.Round(x / (_ln2High + _ln2Low), MidpointRounding.ToEven);
        var r = x - (k * _ln2High) - (k * _ln2Low);

        // Taylor series for eʳ, evaluated by Horner's rule; 18 terms reach full precision for |r| ≤ 0.35.
        var sum = 1.0;
        for (var n = 18; n >= 1; n--)
        {
            sum = 1 + (r * sum / n);
        }

        return ScaleByPowerOfTwo(sum, k);
    }

    /// <summary>The natural logarithm.</summary>
    /// <param name="x">A positive number.</param>
    /// <returns>ln x; negative infinity for 0, NaN for a negative number.</returns>
    public static double Log(double x)
    {
        if (double.IsNaN(x) || x < 0)
        {
            return double.NaN;
        }

        if (x == 0)
        {
            return double.NegativeInfinity;
        }

        if (double.IsPositiveInfinity(x))
        {
            return x;
        }

        // Subnormals: scale into the normal range first.
        var exponentAdjust = 0;
        if (x < double.Epsilon * (1L << 52))
        {
            x *= 1L << 54;
            exponentAdjust = -54;
        }

        // x = m × 2ᵉ with m in [√½, √2).
        var bits = BitConverter.DoubleToInt64Bits(x);
        var e = (int)((bits >> 52) & 0x7FF) - 1023 + exponentAdjust;
        var m = BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFFL) | 0x3FF0000000000000L);
        if (m > 1.4142135623730951)
        {
            m /= 2;
            e++;
        }

        // ln m = 2 artanh s, with s = (m − 1) / (m + 1) and |s| ≤ 0.172.
        var s = (m - 1) / (m + 1);
        var s2 = s * s;
        var series = 0.0;
        for (var n = 41; n >= 3; n -= 2)
        {
            series = s2 * ((1.0 / n) + series);
        }

        var lnM = 2 * (s + (s * series));
        return (e * _ln2High) + ((e * _ln2Low) + lnM);
    }

    /// <summary>The cosine, accurate for |x| up to about a million.</summary>
    /// <param name="x">An angle in radians.</param>
    /// <returns>cos x.</returns>
    public static double Cos(double x)
    {
        if (!double.IsFinite(x))
        {
            return double.NaN;
        }

        // x = q π/2 + r with |r| ≤ π/4, then pick the quadrant.
        var q = Math.Round(x / (_halfPiHigh + _halfPiLow), MidpointRounding.ToEven);
        var r = x - (q * _halfPiHigh) - (q * _halfPiLow);
        var quadrant = (long)q & 3;

        return quadrant switch
        {
            0 => CosNear(r),
            1 => -SinNear(r),
            2 => -CosNear(r),
            _ => SinNear(r)
        };
    }

    private static double CosNear(double r)
    {
        // Taylor series to r¹⁸, enough for |r| ≤ π/4.
        var r2 = r * r;
        var sum = 1.0;
        for (var n = 18; n >= 2; n -= 2)
        {
            sum = 1 - (r2 * sum / (n * (n - 1)));
        }

        return sum;
    }

    private static double SinNear(double r)
    {
        var r2 = r * r;
        var sum = 1.0;
        for (var n = 19; n >= 3; n -= 2)
        {
            sum = 1 - (r2 * sum / (n * (n - 1)));
        }

        return r * sum;
    }

    // 2ᵏ built from its bits, in two steps when k is outside the normal exponent range.
    private static double ScaleByPowerOfTwo(double value, int k)
    {
        while (k > 1023)
        {
            value *= BitConverter.Int64BitsToDouble(0x7FE0000000000000L);
            k -= 1023;
        }

        while (k < -1022)
        {
            value *= BitConverter.Int64BitsToDouble(0x0010000000000000L);
            k += 1022;
        }

        return value * BitConverter.Int64BitsToDouble((long)(k + 1023) << 52);
    }
}
