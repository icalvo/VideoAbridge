using FluentAssertions;

namespace VideoAbridge.Core.Tests;

public class AbridgedTimelineLayoutTests
{
    [Fact]
    public void FromPlanMatchesPreviewTimeMapDurationAndMapsJoins()
    {
        var xfId = Guid.NewGuid();
        var cutId = Guid.NewGuid();

        var plan = new ExportPlan(
            [
                new TimeRange(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(10)),
                new TimeRange(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)),
                new TimeRange(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(50))
            ],
            [
                new ExportJoin(TransitionMode.Crossfade, 2),
                new ExportJoin(TransitionMode.HardCut, 0)
            ],
            FadeFromBlackSeconds: 1.5,
            FadeToBlackSeconds: 1.0);

        var layout = AbridgedTimelineLayout.FromPlan(plan, [xfId, cutId]);
        var map = PreviewTimeMap.FromPlan(plan);

        layout.Duration.TotalSeconds.Should().BeApproximately(map.AbridgedDuration.TotalSeconds, 1e-6);
        // clip0 solo 8 + xf 2 + clip1 solo 8 + cut + clip2 solo 10 = 28
        layout.Duration.TotalSeconds.Should().BeApproximately(28, 1e-6);

        var fadeIn = layout.Overlays
            .Where(IsExterior(ExteriorOverlayKind.FadeIn))
            .Should().ContainSingle().Subject;
        fadeIn.Range.Start.Should().Be(TimeSpan.Zero);
        fadeIn.Range.End.TotalSeconds.Should().BeApproximately(1.5, 1e-6);

        var xf = layout.Overlays
            .Where(IsInterior(TransitionMode.Crossfade, xfId))
            .Should().ContainSingle().Subject;
        xf.Range.Start.TotalSeconds.Should().BeApproximately(8, 1e-6);
        xf.Range.End.TotalSeconds.Should().BeApproximately(10, 1e-6);

        var cut = layout.Overlays
            .Where(IsInterior(TransitionMode.HardCut, cutId))
            .Should().ContainSingle().Subject;
        cut.Range.Start.TotalSeconds.Should().BeApproximately(18, 1e-6);
        cut.Range.End.Should().Be(cut.Range.Start);

        var fadeOut = layout.Overlays
            .Where(IsExterior(ExteriorOverlayKind.FadeOut))
            .Should().ContainSingle().Subject;
        fadeOut.Range.End.Should().Be(layout.Duration);
        fadeOut.Range.Start.TotalSeconds.Should().BeApproximately(27, 1e-6);
    }

    [Fact]
    public void FromTimelineAttachesInteriorDeletedRunIds()
    {
        var timeline = Timeline.Create(TimeSpan.FromSeconds(100)).RequireOk();
        timeline = timeline.AddMarker(TimeSpan.FromSeconds(10), MarkerSource.User).RequireOk();
        timeline = timeline.AddMarker(TimeSpan.FromSeconds(20), MarkerSource.User).RequireOk();
        timeline = timeline.AddMarker(TimeSpan.FromSeconds(30), MarkerSource.User).RequireOk();
        timeline = timeline.AddMarker(TimeSpan.FromSeconds(40), MarkerSource.User).RequireOk();

        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).RequireOk();
        timeline = timeline.DeleteBetween(timeline.Markers[2].Id, timeline.Markers[3].Id).RequireOk();

        var runs = timeline.GetDeletedRuns().Where(r => r.IsInterior).ToList();
        runs.Should().HaveCount(2);
        timeline = timeline.SetJoinCrossfade(runs[0].Id, 1.0).RequireOk();

        var layout = AbridgedTimelineLayout.FromTimeline(timeline);
        layout.Overlays.Where(IsInterior(TransitionMode.Crossfade, runs[0].Id)).Should().ContainSingle();
        layout.Overlays.Where(IsInterior(TransitionMode.HardCut, runs[1].Id)).Should().ContainSingle();
    }

    [Fact]
    public void FadeLongerThanDurationIsClamped()
    {
        var plan = new ExportPlan(
            [new TimeRange(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(2))],
            [],
            FadeFromBlackSeconds: 5,
            FadeToBlackSeconds: 5);

        var layout = AbridgedTimelineLayout.FromPlan(plan);
        layout.Duration.TotalSeconds.Should().BeApproximately(2, 1e-6);
        layout.Overlays.Where(IsExterior(ExteriorOverlayKind.FadeIn)).Should().ContainSingle()
            .Which.Range.End.Should().Be(layout.Duration);
        layout.Overlays.Where(IsExterior(ExteriorOverlayKind.FadeOut)).Should().ContainSingle()
            .Which.Range.Start.Should().Be(TimeSpan.Zero);
    }

    private static Func<AbridgedOverlay, bool> IsExterior(ExteriorOverlayKind kind) =>
        overlay => overlay.Value is ExteriorOverlay exterior && exterior.Kind == kind;

    private static Func<AbridgedOverlay, bool> IsInterior(TransitionMode mode, Guid deletedRunId) =>
        overlay => overlay.Value is InteriorOverlay interior
            && interior.Mode == mode
            && interior.DeletedRunId == deletedRunId;
}
