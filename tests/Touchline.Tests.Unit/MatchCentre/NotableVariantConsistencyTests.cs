using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
using Touchline.Domain.Teams;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Demo;
using Touchline.MatchCentre.Live;
using Touchline.MatchCentre.Storage;

namespace Touchline.Tests.Unit.MatchCentre;

/// <summary>The live projector and the demo source pick the same template variant for the same moment (ADR-070).</summary>
[TestFixture]
public class NotableVariantConsistencyTests
{
    private static readonly IReadOnlyDictionary<string, TeamProfile> _teams = TeamProfileCatalog.LoadEmbedded();

    private static Viewer ViewerOf(ViewingMode mode) =>
        Viewer.Create(mode, "en", null, null, null, _ => true, _ => true).Viewer!;

    private static async Task<IReadOnlyList<MomentView>> DemoMoments(ViewingMode mode)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 14, 0, 0, TimeSpan.Zero));
        var source = new DemoMatchSource(
            DemoRecording.Events, _teams, new KeyMomentDetector(teams: _teams.Values),
            Options.Create(new DemoOptions { Speed = 1, LapGap = TimeSpan.FromMinutes(1) }), time);
        time.Advance(TimeSpan.FromHours(3));
        return (await source.GetMomentsAsync("demo-1", ViewerOf(mode), CancellationToken.None))!;
    }

    [Test]
    public async Task The_projector_and_the_demo_source_say_the_same_line_for_every_notable_moment()
    {
        var detector = new KeyMomentDetector(teams: _teams.Values);
        var projector = new MatchStreamProjector(DemoRecording.Events[0].MatchId, detector, _teams);
        var projected = DemoRecording.Events
            .Select(projector.Apply)
            .Where(p => p.Notable is not null)
            .ToDictionary(
                p => p.Notable!.Trigger.Sequence,
                p => CommentarySelection.Pick(NotableLines.For(p.Notable!, "demo-1"), ViewingMode.Analyst, "en")!.Text);

        var demo = (await DemoMoments(ViewingMode.Analyst)).Where(m => m.Tier == MomentTier.Notable).ToList();

        demo.Should().NotBeEmpty();
        demo.Should().OnlyContain(m => projected[m.Sequence] == m.Commentary!.Text);
        demo.Where(m => m.Kind == "Corner").Select(m => m.Commentary!.Text).Distinct().Should().HaveCountGreaterThan(1, "a run of corners no longer repeats one line");
    }

    [Test]
    public async Task A_casual_viewer_sees_the_same_corner_wording_as_an_analyst_even_though_minor_moments_are_hidden()
    {
        var analyst = (await DemoMoments(ViewingMode.Analyst)).Where(m => m.Kind == "Corner").ToDictionary(m => m.Sequence, m => m.Commentary!.Text);
        var casual = (await DemoMoments(ViewingMode.Casual)).Where(m => m.Kind == "Corner").ToList();

        casual.Should().NotBeEmpty();
        casual.Should().OnlyContain(m => analyst[m.Sequence] == m.Commentary!.Text);
    }
}
