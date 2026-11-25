using FluentAssertions;

namespace VideoAbridge.Core.Tests;

public class TimelineTests
{
    private static Timeline CreateWithMarkers(params double[] seconds)
    {
        var timeline = Timeline.Create(TimeSpan.FromSeconds(100)).RequireOk();
        foreach (var s in seconds)
            timeline = timeline.AddMarker(TimeSpan.FromSeconds(s), MarkerSource.User).RequireOk();

        return timeline;
    }

    [Fact]
    public void CreateHasSingleKeptSegment()
    {
        var timeline = Timeline.Create(TimeSpan.FromSeconds(60)).RequireOk();
        timeline.Segments.Should().ContainSingle().Which.IsDeleted.Should().BeFalse();
        timeline.Markers.Should().BeEmpty();
        timeline.Joins.Should().BeEmpty();
    }

    [Fact]
    public void CreateRejectsNonPositiveDuration()
    {
        var error = Timeline.Create(TimeSpan.Zero).RequireError();
        error.Code.Should().Be("InvalidDuration");
    }

    [Fact]
    public void FromPersistedStateRejectsMarkerOutsideDuration()
    {
        var error = Timeline.FromPersistedState(
                TimeSpan.FromSeconds(60),
                [
                    new Marker(MarkerId.New(), TimeSpan.FromSeconds(30), MarkerSource.User),
                    new Marker(MarkerId.New(), TimeSpan.FromSeconds(60), MarkerSource.User)
                ],
                [false, false, false],
                [],
                ProjectSettings.Default)
            .RequireError();

        error.Code.Should().Be("MarkerOutOfRange");
    }

    [Fact]
    public void FromPersistedStateRejectsMarkerAtOrBeforeZero()
    {
        var error = Timeline.FromPersistedState(
                TimeSpan.FromSeconds(60),
                [new Marker(MarkerId.New(), TimeSpan.Zero, MarkerSource.User)],
                [false, false],
                [],
                ProjectSettings.Default)
            .RequireError();

        error.Code.Should().Be("MarkerOutOfRange");
    }

    [Fact]
    public void FromPersistedStateAcceptsMarkersInsideDuration()
    {
        var timeline = Timeline.FromPersistedState(
                TimeSpan.FromSeconds(60),
                [
                    new Marker(MarkerId.New(), TimeSpan.FromSeconds(10), MarkerSource.User),
                    new Marker(MarkerId.New(), TimeSpan.FromSeconds(50), MarkerSource.SceneDetect)
                ],
                [false, true, false],
                [new JoinTransition(DeletedRunId.New(), TransitionMode.HardCut, TimeSpan.Zero)],
                ProjectSettings.Default)
            .RequireOk();

        timeline.Markers.Should().HaveCount(2);
        timeline.Segments.Should().HaveCount(3);
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
    public void MoveMarkerUpdatesTimeWithinNeighborsAndPreservesId()
    {
        var timeline = CreateWithMarkers(10, 30, 50);
        var id = timeline.Markers[1].Id;

        var moved = timeline.MoveMarker(id, TimeSpan.FromSeconds(40)).RequireOk();

        moved.Markers[1].Id.Should().Be(id);
        moved.Markers[1].Time.Should().Be(TimeSpan.FromSeconds(40));
        moved.Markers.Select(m => m.Id).Should().Equal(timeline.Markers.Select(m => m.Id));
        moved.CanMoveMarker(id, TimeSpan.FromSeconds(40)).Should().BeTrue();
        moved.GetMoveMarkerBlockReason(id, TimeSpan.FromSeconds(40)).Should().BeNull();
    }

    [Fact]
    public void MoveMarkerAllowsKeepDeleteBoundaryWithinNeighbors()
    {
        var timeline = CreateWithMarkers(10, 30, 50);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).RequireOk();
        var boundaryId = timeline.Markers[1].Id;

        var moved = timeline.MoveMarker(boundaryId, TimeSpan.FromSeconds(40)).RequireOk();

        moved.Markers[1].Time.Should().Be(TimeSpan.FromSeconds(40));
        moved.GetDeletedRuns().Should().ContainSingle()
            .Which.Range.End.Should().Be(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public void TryGetDeletedRunBoundariesReturnsMarkerIds()
    {
        var timeline = CreateWithMarkers(10, 30, 50);
        var leftId = timeline.Markers[0].Id;
        var rightId = timeline.Markers[1].Id;
        timeline = timeline.DeleteBetween(leftId, rightId).RequireOk();
        var runId = timeline.GetDeletedRuns().Single().Id;

        timeline.TryGetDeletedRunBoundaries(runId, out var left, out var right).Should().BeTrue();
        left.Should().Be(leftId);
        right.Should().Be(rightId);
    }

    [Fact]
    public void MoveMarkerChainSlidesDeletedRunWhenBothBoundsMove()
    {
        var timeline = CreateWithMarkers(10, 30, 60);
        var leftId = timeline.Markers[0].Id;
        var rightId = timeline.Markers[1].Id;
        timeline = timeline.DeleteBetween(leftId, rightId).RequireOk();

        var delta = TimeSpan.FromSeconds(5);
        var moved = timeline
            .MoveMarker(rightId, TimeSpan.FromSeconds(30) + delta)
            .Bind(t => t.MoveMarker(leftId, TimeSpan.FromSeconds(10) + delta))
            .RequireOk();

        moved.GetDeletedRuns().Should().ContainSingle()
            .Which.Range.Should().Be(new TimeRange(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(35)));
    }

    [Fact]
    public void MoveMarkerFailsWhenCrossingNeighbor()
    {
        var timeline = CreateWithMarkers(10, 30, 50);
        var error = timeline.MoveMarker(timeline.Markers[1].Id, TimeSpan.FromSeconds(55)).RequireError();
        error.Code.Should().Be("MarkerTooClose");
        timeline.CanMoveMarker(timeline.Markers[1].Id, TimeSpan.FromSeconds(55)).Should().BeFalse();
    }

    [Fact]
    public void MoveMarkerFailsWhenTooCloseToNeighbor()
    {
        var timeline = CreateWithMarkers(10, 30, 50);
        var error = timeline.MoveMarker(
                timeline.Markers[1].Id,
                TimeSpan.FromSeconds(50) - Timeline.MarkerEpsilon)
            .RequireError();
        error.Code.Should().Be("MarkerTooClose");
    }

    [Fact]
    public void MoveMarkerFailsWhenOutOfRange()
    {
        var timeline = CreateWithMarkers(10);
        var error = timeline.MoveMarker(timeline.Markers[0].Id, TimeSpan.Zero).RequireError();
        error.Code.Should().Be("MarkerOutOfRange");
    }

    [Fact]
    public void MoveMarkerAllowsSubEpsilonNudge()
    {
        // 1 frame at 30fps is ~33ms, under MarkerEpsilon (40ms); must still apply.
        var timeline = CreateWithMarkers(10, 30);
        var id = timeline.Markers[0].Id;
        var frame = TimeSpan.FromSeconds(1.0 / 30.0);

        var moved = timeline.MoveMarker(id, TimeSpan.FromSeconds(10) + frame).RequireOk();

        moved.Markers[0].Time.Should().Be(TimeSpan.FromSeconds(10) + frame);
    }

    [Fact]
    public void MoveMarkerNoOpWhenAlreadyAtTime()
    {
        var timeline = CreateWithMarkers(10, 30);
        var same = timeline.MoveMarker(timeline.Markers[0].Id, TimeSpan.FromSeconds(10)).RequireOk();
        same.Should().BeSameAs(timeline);
    }

    [Fact]
    public void MoveMarkerFailsWhenItWouldBreakCrossfade()
    {
        // keep 0-5 (5s), delete 5-10, keep 10-100 → crossfade cap 5
        var timeline = CreateWithMarkers(5, 10);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).RequireOk();
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del.Id, TimeSpan.FromSeconds(4.0)).RequireOk();

        // Move left boundary toward start → first kept becomes 3s < 4s crossfade
        var error = timeline.MoveMarker(timeline.Markers[0].Id, TimeSpan.FromSeconds(3)).RequireError();
        error.Code.Should().Be("CrossfadeOutOfRange");
    }

    [Fact]
    public void MoveMarkerFailsWhenItWouldBreakStartFade()
    {
        // keep 0-10, delete 10-20, keep 20-100 → start-fade cap is first kept (10s)
        var timeline = CreateWithMarkers(10, 20);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).RequireOk();
        timeline = timeline.SetStartFade(8.0).RequireOk();

        // Move left boundary left → first kept becomes 5s < 8s start fade
        var error = timeline.MoveMarker(timeline.Markers[0].Id, TimeSpan.FromSeconds(5)).RequireError();
        error.Code.Should().Be("StartFadeOutOfRange");
    }

    [Fact]
    public void MoveMarkerFailsWhenItWouldBreakEndFade()
    {
        // keep 0-80, delete 80-90, keep 90-100 → end-fade cap is last kept (10s)
        var timeline = CreateWithMarkers(80, 90);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).RequireOk();
        timeline = timeline.SetEndFade(8.0).RequireOk();

        // Move right boundary right → last kept becomes 5s < 8s end fade
        var error = timeline.MoveMarker(timeline.Markers[1].Id, TimeSpan.FromSeconds(95)).RequireError();
        error.Code.Should().Be("EndFadeOutOfRange");
    }

    [Fact]
    public void MoveMarkerSucceedsWhenTransitionsStillFit()
    {
        var timeline = CreateWithMarkers(5, 10);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).RequireOk();
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del.Id, TimeSpan.FromSeconds(2.0)).RequireOk();
        timeline = timeline.SetStartFade(1.0).RequireOk();

        var moved = timeline.MoveMarker(timeline.Markers[0].Id, TimeSpan.FromSeconds(4)).RequireOk();
        moved.Markers[0].Time.Should().Be(TimeSpan.FromSeconds(4));
        moved.Joins[0].Crossfade.Should().Be(TimeSpan.FromSeconds(2.0));
        moved.Settings.FadeFromBlackSeconds.Should().Be(1.0);
    }

    [Fact]
    public void RemoveMarkerFailsOnKeepDeleteBoundary()
    {
        var timeline = CreateWithMarkers(10, 20);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).RequireOk();

        timeline.CanRemoveMarker(timeline.Markers[0].Id).Should().BeFalse();
        timeline.GetRemoveMarkerBlockReason(timeline.Markers[0].Id)
            .Should().Contain("separates keep and delete");
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

        var join = result.GetDeletedRuns().Should().ContainSingle(r => r.IsInterior).Subject.Join;
        join.Should().NotBeNull();
        join.Mode.Should().Be(TransitionMode.HardCut);
    }

    [Fact]
    public void RangeContainsSegmentReflectsDeletedFlags()
    {
        var timeline = CreateWithMarkers(10, 20);
        var left = timeline.Markers[0].Id;
        var right = timeline.Markers[1].Id;

        timeline.RangeContainsSegment(MarkerId.StartBound, left, isDeleted: false).Should().BeTrue();
        timeline.RangeContainsSegment(MarkerId.StartBound, left, isDeleted: true).Should().BeFalse();

        timeline = timeline.DeleteBetween(MarkerId.StartBound, left).RequireOk();
        timeline.RangeContainsSegment(MarkerId.StartBound, left, isDeleted: false).Should().BeFalse();
        timeline.RangeContainsSegment(MarkerId.StartBound, left, isDeleted: true).Should().BeTrue();
        timeline.RangeContainsSegment(left, right, isDeleted: false).Should().BeTrue();
    }

    [Fact]
    public void TryGetSurroundingBoundariesFindsNeighbors()
    {
        var timeline = CreateWithMarkers(10, 20, 40);

        timeline.TryGetSurroundingBoundaries(TimeSpan.FromSeconds(5), out var left, out var right)
            .Should().BeTrue();
        left.Should().Be(MarkerId.StartBound);
        right.Should().Be(timeline.Markers[0].Id);

        timeline.TryGetSurroundingBoundaries(TimeSpan.FromSeconds(25), out left, out right)
            .Should().BeTrue();
        left.Should().Be(timeline.Markers[1].Id);
        right.Should().Be(timeline.Markers[2].Id);

        timeline.TryGetSurroundingBoundaries(TimeSpan.FromSeconds(50), out left, out right)
            .Should().BeTrue();
        left.Should().Be(timeline.Markers[2].Id);
        right.Should().Be(MarkerId.EndBound);
    }

    [Fact]
    public void TryGetSurroundingBoundariesRejectsExactBoundary()
    {
        var timeline = CreateWithMarkers(10, 20);

        timeline.TryGetSurroundingBoundaries(TimeSpan.Zero, out _, out _).Should().BeFalse();
        timeline.TryGetSurroundingBoundaries(timeline.Duration, out _, out _).Should().BeFalse();
        timeline.TryGetSurroundingBoundaries(TimeSpan.FromSeconds(10), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetSurroundingBoundariesWithNoMarkersSpansWholeTimeline()
    {
        var timeline = Timeline.Create(TimeSpan.FromSeconds(60)).RequireOk();

        timeline.TryGetSurroundingBoundaries(TimeSpan.FromSeconds(30), out var left, out var right)
            .Should().BeTrue();
        left.Should().Be(MarkerId.StartBound);
        right.Should().Be(MarkerId.EndBound);
    }

    [Fact]
    public void CoalesceDeletedRunsResetsToHardCut()
    {
        var timeline = CreateWithMarkers(10, 20, 30, 40, 50);
        // Del1 between 10-20 with crossfade, Del2 between 30-40 hard cut, keep 20-30
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).RequireOk();
        var del1 = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del1.Id, TimeSpan.FromSeconds(1.0)).RequireOk();

        timeline = timeline.DeleteBetween(timeline.Markers[2].Id, timeline.Markers[3].Id).RequireOk();
        timeline.Joins.Should().HaveCount(2);
        timeline.Joins[0].Mode.Should().Be(TransitionMode.Crossfade);

        // Delete keep between them (20-30)
        timeline = timeline.DeleteBetween(timeline.Markers[1].Id, timeline.Markers[2].Id).RequireOk();
        timeline.Joins.Should().ContainSingle();
        timeline.Joins[0].Mode.Should().Be(TransitionMode.HardCut);
        timeline.Joins[0].Crossfade.Should().Be(TimeSpan.FromSeconds(0));
    }

    [Fact]
    public void ExpandSingleDeletedRunPreservesCrossfade()
    {
        var timeline = CreateWithMarkers(10, 20, 30, 40);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).RequireOk();
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del.Id, TimeSpan.FromSeconds(2.0)).RequireOk();

        // Expand into next keep segment 20-30
        timeline = timeline.DeleteBetween(timeline.Markers[1].Id, timeline.Markers[2].Id).RequireOk();
        timeline.Joins.Should().ContainSingle();
        timeline.Joins[0].Mode.Should().Be(TransitionMode.Crossfade);
        timeline.Joins[0].DeletedRunId.Should().Be(del.Id);
        timeline.Joins[0].Crossfade.Should().Be(TimeSpan.FromSeconds(2.0));
    }

    [Fact]
    public void SplitDeletedRunBothBecomeHardCut()
    {
        var timeline = CreateWithMarkers(10, 20, 30, 40);
        // One deleted run covering 10-40
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[3].Id).RequireOk();
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del.Id, TimeSpan.FromSeconds(1.0)).RequireOk();

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

        var fail = timeline.SetJoinCrossfade(del.Id, TimeSpan.FromSeconds(6.0)).RequireError();
        fail.Code.Should().Be("CrossfadeOutOfRange");

        timeline.SetJoinCrossfade(del.Id, TimeSpan.FromSeconds(5.0)).RequireOk();
    }

    [Fact]
    public void LeadingDeleteHasNoJoin()
    {
        var timeline = CreateWithMarkers(10, 20);
        timeline = timeline.DeleteBetween(MarkerId.StartBound, timeline.Markers[0].Id).RequireOk();
        timeline.Joins.Should().BeEmpty();
        timeline.GetDeletedRuns().Single().IsInterior.Should().BeFalse();
    }

    [Fact]
    public void EdgeDeletedRunsHaveStableIds()
    {
        var timeline = CreateWithMarkers(10, 20, 30);
        timeline = timeline.DeleteBetween(MarkerId.StartBound, timeline.Markers[0].Id).RequireOk();
        timeline = timeline.DeleteBetween(timeline.Markers[2].Id, MarkerId.EndBound).RequireOk();

        var runs = timeline.GetDeletedRuns().Where(r => !r.IsInterior).ToList();
        runs.Should().HaveCount(2);
        runs.Should().ContainSingle(r => r.Id == DeletedRunId.Leading);
        runs.Should().ContainSingle(r => r.Id == DeletedRunId.Trailing);

        // IDs stay stable across repeated queries.
        timeline.GetDeletedRuns().Select(r => r.Id).Should().BeEquivalentTo(
            timeline.GetDeletedRuns().Select(r => r.Id));
    }

    [Fact]
    public void RestoreDeletedRunRestoresLeadingAndTrailing()
    {
        var timeline = CreateWithMarkers(10, 20, 30);
        timeline = timeline.DeleteBetween(MarkerId.StartBound, timeline.Markers[0].Id).RequireOk();
        timeline = timeline.DeleteBetween(timeline.Markers[2].Id, MarkerId.EndBound).RequireOk();

        timeline = timeline.RestoreDeletedRun(DeletedRunId.Leading).RequireOk();
        timeline.GetDeletedRuns().Should().ContainSingle(r => r.Id == DeletedRunId.Trailing);

        timeline = timeline.RestoreDeletedRun(DeletedRunId.Trailing).RequireOk();
        timeline.GetDeletedRuns().Should().BeEmpty();
        timeline.Segments.Should().OnlyContain(s => !s.IsDeleted);
    }

    [Fact]
    public void TryBuildExportPlanFailsWhenEverythingDeleted()
    {
        var timeline = CreateWithMarkers(10);
        timeline = timeline.DeleteBetween(MarkerId.StartBound, MarkerId.EndBound).RequireOk();
        var error = timeline.TryBuildExportPlan().RequireError();
        error.Code.Should().Be("NothingToExport");
    }

    [Fact]
    public void TryBuildExportPlanSucceedsWithJoins()
    {
        var timeline = CreateWithMarkers(10, 20, 30);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).RequireOk();
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del.Id, TimeSpan.FromSeconds(1.5)).RequireOk();

        var plan = timeline.TryBuildExportPlan().RequireOk();
        plan.Clips.Should().HaveCount(2);
        plan.JoinsBetweenClips.Should().ContainSingle()
            .Which.Mode.Should().Be(TransitionMode.Crossfade);
        plan.JoinsBetweenClips[0].Crossfade.Should().Be(TimeSpan.FromSeconds(1.5));
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
        var timeline = Timeline.Create(TimeSpan.FromSeconds(10)).RequireOk();
        var act = () => timeline.RemoveMarker(MarkerId.New());
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

    [Fact]
    public void GetTransitionShadeRangesCoversFadesAndCrossfadeTails()
    {
        // keep 0-10, delete 10-20, keep 20-100
        var timeline = CreateWithMarkers(10, 20);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).RequireOk();
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del.Id, TimeSpan.FromSeconds(3.0)).RequireOk();
        timeline = timeline.SetStartFade(2.0).RequireOk();
        timeline = timeline.SetEndFade(1.5).RequireOk();

        var shades = timeline.GetTransitionShadeRanges();
        shades.Should().HaveCount(4);

        shades[0].Start.Should().Be(TimeSpan.Zero);
        shades[0].End.TotalSeconds.Should().BeApproximately(2.0, 1e-6);

        shades[1].Start.TotalSeconds.Should().BeApproximately(98.5, 1e-6);
        shades[1].End.TotalSeconds.Should().BeApproximately(100.0, 1e-6);

        shades[2].Start.TotalSeconds.Should().BeApproximately(7.0, 1e-6);
        shades[2].End.TotalSeconds.Should().BeApproximately(10.0, 1e-6);

        shades[3].Start.TotalSeconds.Should().BeApproximately(20.0, 1e-6);
        shades[3].End.TotalSeconds.Should().BeApproximately(23.0, 1e-6);
    }
}
