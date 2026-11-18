using FluentAssertions;

namespace VideoAbridge.Core.Tests;

public class TimelineTests
{
    private static Timeline CreateWithMarkers(params double[] seconds)
    {
        var timeline = Timeline.Create(TimeSpan.FromSeconds(100));
        foreach (var s in seconds)
            timeline = timeline.AddMarker(TimeSpan.FromSeconds(s), MarkerSource.User).RequireOk();

        return timeline;
    }

    [Fact]
    public void CreateHasSingleKeptSegment()
    {
        var timeline = Timeline.Create(TimeSpan.FromSeconds(60));
        timeline.Segments.Should().ContainSingle().Which.IsDeleted.Should().BeFalse();
        timeline.Markers.Should().BeEmpty();
        timeline.Joins.Should().BeEmpty();
    }

    [Fact]
    public void AddMarkerSplitsSegmentInheritingState()
    {
        var timeline = CreateWithMarkers(10, 20, 30);
        timeline.Segments.Should().HaveCount(4);

        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[2].Id).RequireOk();

        var add = timeline.AddMarker(TimeSpan.FromSeconds(25), MarkerSource.User).RequireOk();
        var segmentAround = add.Segments[2];
        segmentAround.IsDeleted.Should().BeTrue();
        add.Joins.Should().HaveCount(1); // still one interior deleted run
    }

    [Fact]
    public void AddMarkerFailsWhenTooClose()
    {
        var timeline = CreateWithMarkers(10);
        var error = timeline.AddMarker(TimeSpan.FromSeconds(10.01), MarkerSource.User).RequireError();
        error.Code.Should().Be("MarkerTooClose");
    }

    [Fact]
    public void RemoveMarkerFailsOnKeepDeleteBoundary()
    {
        var timeline = CreateWithMarkers(10, 20);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).RequireOk();

        var error = timeline.RemoveMarker(timeline.Markers[0].Id).RequireError();
        error.Code.Should().Be("MarkerIsBoundary");
    }

    [Fact]
    public void RemoveMarkerSucceedsWhenBothSidesKept()
    {
        var timeline = CreateWithMarkers(10, 20);
        var result = timeline.RemoveMarker(timeline.Markers[0].Id).RequireOk();
        result.Markers.Should().ContainSingle();
        result.Segments.Should().HaveCount(2);
    }

    [Fact]
    public void DeleteBetweenCreatesInteriorHardCut()
    {
        var timeline = CreateWithMarkers(10, 20, 30);
        var result = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).RequireOk();

        var runs = result.GetDeletedRuns();
        runs.Should().ContainSingle(r => r.IsInterior);
        runs[0].Join!.Mode.Should().Be(TransitionMode.HardCut);
    }

    [Fact]
    public void CoalesceDeletedRunsResetsToHardCut()
    {
        var timeline = CreateWithMarkers(10, 20, 30, 40, 50);
        // Del1 between 10-20 with crossfade, Del2 between 30-40 hard cut, keep 20-30
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).RequireOk();
        var del1 = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del1.Id, 1.0).RequireOk();

        timeline = timeline.DeleteBetween(timeline.Markers[2].Id, timeline.Markers[3].Id).RequireOk();
        timeline.Joins.Should().HaveCount(2);
        timeline.Joins[0].Mode.Should().Be(TransitionMode.Crossfade);

        // Delete keep between them (20-30)
        timeline = timeline.DeleteBetween(timeline.Markers[1].Id, timeline.Markers[2].Id).RequireOk();
        timeline.Joins.Should().ContainSingle();
        timeline.Joins[0].Mode.Should().Be(TransitionMode.HardCut);
        timeline.Joins[0].CrossfadeSeconds.Should().Be(0);
    }

    [Fact]
    public void ExpandSingleDeletedRunPreservesCrossfade()
    {
        var timeline = CreateWithMarkers(10, 20, 30, 40);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).RequireOk();
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del.Id, 2.0).RequireOk();

        // Expand into next keep segment 20-30
        timeline = timeline.DeleteBetween(timeline.Markers[1].Id, timeline.Markers[2].Id).RequireOk();
        timeline.Joins.Should().ContainSingle();
        timeline.Joins[0].Mode.Should().Be(TransitionMode.Crossfade);
        timeline.Joins[0].DeletedRunId.Should().Be(del.Id);
        timeline.Joins[0].CrossfadeSeconds.Should().Be(2.0);
    }

    [Fact]
    public void SplitDeletedRunBothBecomeHardCut()
    {
        var timeline = CreateWithMarkers(10, 20, 30, 40);
        // One deleted run covering 10-40
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[3].Id).RequireOk();
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del.Id, 1.0).RequireOk();

        // Restore middle 20-30 → two deleted runs (10-20 and 30-40)
        timeline = timeline.RestoreBetween(timeline.Markers[1].Id, timeline.Markers[2].Id).RequireOk();
        timeline.Joins.Should().HaveCount(2);
        timeline.Joins.Should().OnlyContain(j => j.Mode == TransitionMode.HardCut);
    }

    [Fact]
    public void SetJoinCrossfadeFailsWhenOverCap()
    {
        var timeline = CreateWithMarkers(5, 10);
        // keep 0-5 (5s), delete 5-10, keep 10-100 (90s) → cap = 5
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).RequireOk();
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);

        var fail = timeline.SetJoinCrossfade(del.Id, 6.0).RequireError();
        fail.Code.Should().Be("CrossfadeOutOfRange");

        timeline.SetJoinCrossfade(del.Id, 5.0).RequireOk();
    }

    [Fact]
    public void LeadingDeleteHasNoJoin()
    {
        var timeline = CreateWithMarkers(10, 20);
        timeline = timeline.DeleteBetween(Timeline.StartBoundId, timeline.Markers[0].Id).RequireOk();
        timeline.Joins.Should().BeEmpty();
        timeline.GetDeletedRuns().Single().IsInterior.Should().BeFalse();
    }

    [Fact]
    public void EdgeDeletedRunsHaveStableIds()
    {
        var timeline = CreateWithMarkers(10, 20, 30);
        timeline = timeline.DeleteBetween(Timeline.StartBoundId, timeline.Markers[0].Id).RequireOk();
        timeline = timeline.DeleteBetween(timeline.Markers[2].Id, Timeline.EndBoundId).RequireOk();

        var runs = timeline.GetDeletedRuns().Where(r => !r.IsInterior).ToList();
        runs.Should().HaveCount(2);
        runs.Should().ContainSingle(r => r.Id == Timeline.LeadingDeletedRunId);
        runs.Should().ContainSingle(r => r.Id == Timeline.TrailingDeletedRunId);

        // IDs stay stable across repeated queries.
        timeline.GetDeletedRuns().Select(r => r.Id).Should().BeEquivalentTo(
            timeline.GetDeletedRuns().Select(r => r.Id));
    }

    [Fact]
    public void RestoreDeletedRunRestoresLeadingAndTrailing()
    {
        var timeline = CreateWithMarkers(10, 20, 30);
        timeline = timeline.DeleteBetween(Timeline.StartBoundId, timeline.Markers[0].Id).RequireOk();
        timeline = timeline.DeleteBetween(timeline.Markers[2].Id, Timeline.EndBoundId).RequireOk();

        timeline = timeline.RestoreDeletedRun(Timeline.LeadingDeletedRunId).RequireOk();
        timeline.GetDeletedRuns().Should().ContainSingle(r => r.Id == Timeline.TrailingDeletedRunId);

        timeline = timeline.RestoreDeletedRun(Timeline.TrailingDeletedRunId).RequireOk();
        timeline.GetDeletedRuns().Should().BeEmpty();
        timeline.Segments.Should().OnlyContain(s => !s.IsDeleted);
    }

    [Fact]
    public void TryBuildExportPlanFailsWhenEverythingDeleted()
    {
        var timeline = CreateWithMarkers(10);
        timeline = timeline.DeleteBetween(Timeline.StartBoundId, Timeline.EndBoundId).RequireOk();
        var error = timeline.TryBuildExportPlan().RequireError();
        error.Code.Should().Be("NothingToExport");
    }

    [Fact]
    public void TryBuildExportPlanSucceedsWithJoins()
    {
        var timeline = CreateWithMarkers(10, 20, 30);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).RequireOk();
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del.Id, 1.5).RequireOk();

        var plan = timeline.TryBuildExportPlan().RequireOk();
        plan.Clips.Should().HaveCount(2);
        plan.JoinsBetweenClips.Should().ContainSingle()
            .Which.Mode.Should().Be(TransitionMode.Crossfade);
        plan.JoinsBetweenClips[0].CrossfadeSeconds.Should().Be(1.5);
    }

    [Fact]
    public void SetEndFadeFailsWhenTooLong()
    {
        var timeline = CreateWithMarkers(90);
        // final keep after deleting 0-90 would be 10s; without delete final is 100
        timeline.SetEndFade(101).RequireError();
    }

    [Fact]
    public void OperationsDoNotThrowOnInvalidInput()
    {
        var timeline = Timeline.Create(TimeSpan.FromSeconds(10));
        var act = () => timeline.RemoveMarker(Guid.NewGuid());
        act.Should().NotThrow();
        act().RequireError();
    }

    [Fact]
    public void StructuralSharingUnaffectedMarkersPreserved()
    {
        var timeline = CreateWithMarkers(10, 20, 30);
        var marker1 = timeline.Markers[0];
        var marker3 = timeline.Markers[2];

        // Delete middle segment; flanking markers stay shared instances.
        var next = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).RequireOk();
        ReferenceEquals(next.Markers[0], marker1).Should().BeTrue();
        ReferenceEquals(next.Markers[2], marker3).Should().BeTrue();
    }

    [Fact]
    public void SetStartFadeFailsWhenTooLong()
    {
        var timeline = CreateWithMarkers(90);
        timeline.SetStartFade(101).RequireError();
    }

    [Fact]
    public void SnapToNearestKeptSnapsDeletedTimesToEdges()
    {
        var timeline = CreateWithMarkers(10, 20, 40, 50);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).RequireOk();
        timeline = timeline.DeleteBetween(timeline.Markers[2].Id, timeline.Markers[3].Id).RequireOk();

        timeline.IsKeptTime(TimeSpan.FromSeconds(5)).Should().BeTrue();
        timeline.IsKeptTime(TimeSpan.FromSeconds(15)).Should().BeFalse();
        timeline.SnapToNearestKept(TimeSpan.FromSeconds(15)).Should().Be(TimeSpan.FromSeconds(10));
        timeline.SnapToNearestKept(TimeSpan.FromSeconds(18)).Should().Be(TimeSpan.FromSeconds(20));
        timeline.SnapToNearestKept(TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromSeconds(30));
    }
}
