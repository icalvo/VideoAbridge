using FluentAssertions;
using VideoAbridge.Core;

namespace VideoAbridge.Core.Tests;

public class AbridgedTimelineLayoutTests
{
    [Fact]
    public void FromPlan_MatchesPreviewTimeMapDuration_AndMapsJoins()
    {
        var xfId = Guid.NewGuid();
        var cutId = Guid.NewGuid();

        var plan = new ExportPlan(
            [
                new KeptClip(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(10)),
                new KeptClip(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)),
                new KeptClip(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(50))
            ],
            [
                new ExportJoin(TransitionMode.Crossfade, 2),
                new ExportJoin(TransitionMode.HardCut, 0)
            ],
            FadeFromBlackAtStart: true,
            FadeFromBlackSeconds: 1.5,
            FadeToBlackAtEnd: true,
            FadeToBlackSeconds: 1.0);

        var layout = AbridgedTimelineLayout.FromPlan(plan, [xfId, cutId]);
        var map = PreviewTimeMap.FromPlan(plan);

        layout.Duration.TotalSeconds.Should().BeApproximately(map.AbridgedDuration.TotalSeconds, 1e-6);
        // clip0 solo 8 + xf 2 + clip1 solo 8 + cut + clip2 solo 10 = 28
        layout.Duration.TotalSeconds.Should().BeApproximately(28, 1e-6);

        var fadeIn = layout.Overlays.Should().ContainSingle(o => o.Kind == AbridgedOverlayKind.FadeIn).Subject;
        fadeIn.Start.Should().Be(TimeSpan.Zero);
        fadeIn.End.TotalSeconds.Should().BeApproximately(1.5, 1e-6);

        var xf = layout.Overlays.Should().ContainSingle(o => o.Kind == AbridgedOverlayKind.Crossfade).Subject;
        xf.DeletedRunId.Should().Be(xfId);
        xf.Start.TotalSeconds.Should().BeApproximately(8, 1e-6);
        xf.End.TotalSeconds.Should().BeApproximately(10, 1e-6);

        var cut = layout.Overlays.Should().ContainSingle(o => o.Kind == AbridgedOverlayKind.Cut).Subject;
        cut.DeletedRunId.Should().Be(cutId);
        cut.Start.TotalSeconds.Should().BeApproximately(18, 1e-6);
        cut.End.Should().Be(cut.Start);

        var fadeOut = layout.Overlays.Should().ContainSingle(o => o.Kind == AbridgedOverlayKind.FadeOut).Subject;
        fadeOut.End.Should().Be(layout.Duration);
        fadeOut.Start.TotalSeconds.Should().BeApproximately(27, 1e-6);
    }

    [Fact]
    public void FromTimeline_AttachesInteriorDeletedRunIds()
    {
        var timeline = Timeline.Create(TimeSpan.FromSeconds(100));
        timeline = timeline.AddMarker(TimeSpan.FromSeconds(10), MarkerSource.User).Value!;
        timeline = timeline.AddMarker(TimeSpan.FromSeconds(20), MarkerSource.User).Value!;
        timeline = timeline.AddMarker(TimeSpan.FromSeconds(30), MarkerSource.User).Value!;
        timeline = timeline.AddMarker(TimeSpan.FromSeconds(40), MarkerSource.User).Value!;

        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).Value!;
        timeline = timeline.DeleteBetween(timeline.Markers[2].Id, timeline.Markers[3].Id).Value!;

        var runs = timeline.GetDeletedRuns().Where(r => r.IsInterior).ToList();
        runs.Should().HaveCount(2);
        timeline = timeline.SetJoinCrossfade(runs[0].Id, 1.0).Value!;

        var layout = AbridgedTimelineLayout.FromTimeline(timeline);
        layout.Overlays.Should().Contain(o =>
            o.Kind == AbridgedOverlayKind.Crossfade && o.DeletedRunId == runs[0].Id);
        layout.Overlays.Should().Contain(o =>
            o.Kind == AbridgedOverlayKind.Cut && o.DeletedRunId == runs[1].Id);
    }

    [Fact]
    public void FadeLongerThanDuration_IsClamped()
    {
        var plan = new ExportPlan(
            [new KeptClip(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(2))],
            [],
            FadeFromBlackAtStart: true,
            FadeFromBlackSeconds: 5,
            FadeToBlackAtEnd: true,
            FadeToBlackSeconds: 5);

        var layout = AbridgedTimelineLayout.FromPlan(plan);
        layout.Duration.TotalSeconds.Should().BeApproximately(2, 1e-6);
        layout.Overlays.Should().ContainSingle(o => o.Kind == AbridgedOverlayKind.FadeIn)
            .Which.End.Should().Be(layout.Duration);
        layout.Overlays.Should().ContainSingle(o => o.Kind == AbridgedOverlayKind.FadeOut)
            .Which.Start.Should().Be(TimeSpan.Zero);
    }
}
