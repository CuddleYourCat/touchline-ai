extern alias simulator;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain;
using Touchline.Domain.Teams;

namespace Touchline.Tests.Unit.Simulator;

/// <summary>
/// Pins the whole event stream of a few seeded matches played with no team sheet (ADR-050).
/// The hashes were computed on <c>main</c> before team sheets existed, so a change that moves
/// a single random draw, field or event in the default path fails here. If a tuning change
/// is meant to move the game, recompute them and say so in the ADR. The hashes were computed
/// on Windows: if Linux CI disagrees, check that the engine calls <c>PortableMath</c> wherever
/// it needs a transcendental function (exp, log, cos and so on), because a direct
/// <c>Math.Exp</c> or similar can differ in the last bit across platforms. If one is missing,
/// that's the bug; don't just recompute the hashes on Linux.
/// </summary>
/// <remarks>
/// Recomputed on purpose for substitutions (ADR-064): every event now carries a
/// <c>replacementPlayerId</c> field, the metadata names each bench, and once they come on the
/// substitutes play with their own traits, so every stream moves. Substitution decisions draw
/// from their own seeded stream, so play is unchanged, apart from those fields, until the first substitution.
/// </remarks>
[TestFixture]
public class DefaultLineupGoldenTests
{
    private static readonly IReadOnlyDictionary<string, TeamProfile> _teams = TeamProfileCatalog.LoadEmbedded();

    [TestCase("north-bank-fc", "thames-valley-united", 42, "42AECC0D838C0AAFDAD29F40562A05AB3EF7434C8BF8100DD10AF67033613A6C")]
    [TestCase("north-bank-fc", "thames-valley-united", 1000, "6A7B523D7D4B44ACFCAC7CAA3EED1A3497D154241F1997D8F08F6B067CD962A4")]
    [TestCase("thames-valley-united", "north-bank-fc", 7, "C56F756D36B23506A3EC0571A8583AB373EDAF9475AD6F090E266C445F680932")]
    public void A_match_with_no_team_sheet_plays_exactly_as_before(string home, string away, int seed, string expectedHash)
    {
        var events = new MatchSimulator(_teams[home], _teams[away], seed).Play();

        StreamHash(events).Should().Be(expectedHash);
    }

    [Test]
    public void Null_sheets_are_the_default_path()
    {
        var withNulls = new MatchSimulator(_teams["north-bank-fc"], _teams["thames-valley-united"], 42, new MatchOptions { HomeSheet = null, AwaySheet = null }).Play();
        var without = new MatchSimulator(_teams["north-bank-fc"], _teams["thames-valley-united"], 42).Play();

        StreamHash(withNulls).Should().Be(StreamHash(without));
    }

    // SHA-256 of every event serialised with the shared domain JSON, one per line, so the hash
    // covers every field the pipeline would see.
    private static string StreamHash(IEnumerable<object> events)
    {
        var text = new StringBuilder();
        foreach (var e in events)
        {
            text.Append(JsonSerializer.Serialize(e, e.GetType(), DomainJson.Options)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
