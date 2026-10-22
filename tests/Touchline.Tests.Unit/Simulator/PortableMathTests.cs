extern alias simulator;
using FluentAssertions;
using simulator::Touchline.Simulator.Engine;

namespace Touchline.Tests.Unit.Simulator;

[TestFixture]
public class PortableMathTests
{
    private const double _tolerance = 4e-15;

    // Bit patterns computed once and committed. The same bits on Windows, Linux and macOS
    // prove the functions are portable, which Math.Exp, Log and Cos are not (ADR-001).
    private static readonly (string Function, double Input, long Bits)[] _pinned =
    [
        ("Exp", 1.2345, 4614921088609920645),
        ("Exp", -3.75, 4582435750142741801),
        ("Log", 0.000123, -4602113996733631445),
        ("Log", 0.7071, -4623456929408966050),
        ("Cos", 2.5, -4617980757132604547),
        ("Cos", 5.9, 4606529202575462441)
    ];

    private static IEnumerable<double> Samples(double from, double to, int count) =>
        Enumerable.Range(0, count).Select(i => from + ((to - from) * i / (count - 1)));

    [Test]
    public void Exp_agrees_with_the_platform_to_a_few_units_in_the_last_place()
    {
        foreach (var x in Samples(-700, 700, 20_001))
        {
            Relative(PortableMath.Exp(x), Math.Exp(x)).Should().BeLessThan(_tolerance, $"Exp({x})");
        }
    }

    [Test]
    public void Log_agrees_with_the_platform_to_a_few_units_in_the_last_place()
    {
        var inputs = Samples(1e-6, 1, 20_001).Concat(Samples(1, 1e6, 20_001)).Append(double.Epsilon).Append(1e-310).Append(double.MaxValue);
        foreach (var x in inputs)
        {
            var expected = Math.Log(x);
            (Math.Abs(PortableMath.Log(x) - expected) / Math.Max(1, Math.Abs(expected))).Should().BeLessThan(_tolerance, $"Log({x})");
        }
    }

    [Test]
    public void Cos_agrees_with_the_platform_to_a_few_units_in_the_last_place()
    {
        foreach (var x in Samples(-20, 20, 40_001))
        {
            Math.Abs(PortableMath.Cos(x) - Math.Cos(x)).Should().BeLessThan(_tolerance, $"Cos({x})");
        }
    }

    [Test]
    public void Edge_cases_follow_the_platform()
    {
        PortableMath.Exp(0).Should().Be(1);
        PortableMath.Exp(1000).Should().Be(double.PositiveInfinity);
        PortableMath.Exp(-1000).Should().Be(0);
        PortableMath.Log(1).Should().Be(0);
        PortableMath.Log(0).Should().Be(double.NegativeInfinity);
        double.IsNaN(PortableMath.Log(-1)).Should().BeTrue();
        PortableMath.Cos(0).Should().Be(1);
    }

    [Test]
    public void Results_are_the_same_bits_on_every_platform()
    {
        var actual = _pinned.Select(p => (p.Function, p.Input, Bits: BitConverter.DoubleToInt64Bits(p.Function switch
        {
            "Exp" => PortableMath.Exp(p.Input),
            "Log" => PortableMath.Log(p.Input),
            _ => PortableMath.Cos(p.Input)
        }))).ToList();

        actual.Should().Equal(_pinned);
    }

    private static double Relative(double actual, double expected) =>
        expected == 0 ? Math.Abs(actual) : Math.Abs((actual - expected) / expected);
}
