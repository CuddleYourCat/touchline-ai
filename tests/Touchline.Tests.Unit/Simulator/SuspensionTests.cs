extern alias simulator;
using FluentAssertions;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain.Matches;
using Touchline.Domain.Teams;

namespace Touchline.Tests.Unit.Simulator;

/// <summary>The engine's side of ADR-025: suspended players are left out, and fixtures have their own IDs.</summary>
[TestFixture]
public class SuspensionTests
{
    private static readonly IReadOnlyDictionary<string, TeamProfile> _teams = TeamProfileCatalog.LoadEmbedded();

    private static readonly MatchOptions _fixture = new() { Kind = MatchKind.Fixture, Season = "2026-27", Round = 3 };

    [Test]
    public void A_suspended_player_neither_starts_nor_appears_in_any_event()
    {
        var usual = Play(_fixture);
        var banned = usual[0].Metadata!.Home.StartingEleven[3];

        var match = Play(_fixture with { Unavailable = new HashSet<string> { banned } });

        match[0].Metadata!.Home.StartingEleven.Should().HaveCount(11).And.NotContain(banned);
        match.Should().NotContain(e => e.PlayerId == banned || e.RecipientPlayerId == banned || e.AssistPlayerId == banned);
    }

    [Test]
    public void A_team_without_eleven_available_players_can_not_play()
    {
        var squad = _teams["north-bank-fc"].Squad.Select(p => p.Id).Take(7).ToHashSet();

        var play = () => new MatchSimulator(_teams["north-bank-fc"], _teams["thames-valley-united"], 42, _fixture with { Unavailable = squad });

        play.Should().Throw<ArgumentException>().WithMessage("*available*");
    }

    [Test]
    public void A_fixture_never_shares_an_exhibition_s_match_id()
    {
        var fixture = new MatchSimulator(_teams["north-bank-fc"], _teams["thames-valley-united"], 42, _fixture).MatchId;
        var exhibition = new MatchSimulator(_teams["north-bank-fc"], _teams["thames-valley-united"], 42).MatchId;

        fixture.Should().Be("nbk-tvu-2026-27-r3-42", "the season and round are part of a fixture's identity");
        exhibition.Should().Be("nbk-tvu-42");
    }

    private static List<MatchEvent> Play(MatchOptions options) =>
        [.. new MatchSimulator(_teams["north-bank-fc"], _teams["thames-valley-united"], 42, options).Play()];
}
