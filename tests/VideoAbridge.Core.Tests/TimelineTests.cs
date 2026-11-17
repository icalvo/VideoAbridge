using FluentAssertions;
using VideoAbridge.Core;

namespace VideoAbridge.Core.Tests;

public class TimelineTests
{
    private static Timeline CreateWithMarkers(params double[] seconds)
    {
        var timeline = Timeline.Create(TimeSpan.FromSeconds(100));
        foreach (var s in seconds)
        {
            var result = timeline.AddMarker(TimeSpan.FromSeconds(s), MarkerSource.User);
            result.IsSuccess.Should().BeTrue(result.Error?.Message);
            timeline = result.OkValue;
        }

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

        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[2].Id).OkValue;

        var add = timeline.AddMarker(TimeSpan.FromSeconds(25), MarkerSource.User);
        add.IsSuccess.Should().BeTrue();
        var segmentAround = add.OkValue.Segments[2];
        segmentAround.IsDeleted.Should().BeTrue();
        add.OkValue.Joins.Should().HaveCount(1); // still one interior deleted run
    }

    [Fact]
    public void AddMarkerFailsWhenTooClose()
    {
        var timeline = CreateWithMarkers(10);
        var result = timeline.AddMarker(TimeSpan.FromSeconds(10.01), MarkerSource.User);
        result.IsSuccess.Should().BeFalse();
        result.Error!.Value.Code.Should().Be("MarkerTooClose");
    }

    [Fact]
    public void RemoveMarkerFailsOnKeepDeleteBoundary()
    {
        var timeline = CreateWithMarkers(10, 20);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).OkValue;

        var result = timeline.RemoveMarker(timeline.Markers[0].Id);
        result.IsSuccess.Should().BeFalse();
        result.Error!.Value.Code.Should().Be("MarkerIsBoundary");
    }

    [Fact]
    public void RemoveMarkerSucceedsWhenBothSidesKept()
    {
        var timeline = CreateWithMarkers(10, 20);
        var result = timeline.RemoveMarker(timeline.Markers[0].Id);
        result.IsSuccess.Should().BeTrue();
        result.OkValue.Markers.Should().ContainSingle();
        result.OkValue.Segments.Should().HaveCount(2);
    }

    [Fact]
    public void DeleteBetweenCreatesInteriorHardCut()
    {
        var timeline = CreateWithMarkers(10, 20, 30);
        var result = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id);
        result.IsSuccess.Should().BeTrue();

        var runs = result.OkValue.GetDeletedRuns();
        runs.Should().ContainSingle(r => r.IsInterior);
        runs[0].Join!.Mode.Should().Be(TransitionMode.HardCut);
    }

    [Fact]
    public void CoalesceDeletedRunsResetsToHardCut()
    {
        var timeline = CreateWithMarkers(10, 20, 30, 40, 50);
        // Del1 between 10-20 with crossfade, Del2 between 30-40 hard cut, keep 20-30
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).OkValue;
        var del1 = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del1.Id, 1.0).OkValue;

        timeline = timeline.DeleteBetween(timeline.Markers[2].Id, timeline.Markers[3].Id).OkValue;
        timeline.Joins.Should().HaveCount(2);
        timeline.Joins[0].Mode.Should().Be(TransitionMode.Crossfade);

        // Delete keep between them (20-30)
        timeline = timeline.DeleteBetween(timeline.Markers[1].Id, timeline.Markers[2].Id).OkValue;
        timeline.Joins.Should().ContainSingle();
        timeline.Joins[0].Mode.Should().Be(TransitionMode.HardCut);
        timeline.Joins[0].CrossfadeSeconds.Should().Be(0);
    }

    [Fact]
    public void ExpandSingleDeletedRunPreservesCrossfade()
    {
        var timeline = CreateWithMarkers(10, 20, 30, 40);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).OkValue;
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del.Id, 2.0).OkValue;

        // Expand into next keep segment 20-30
        timeline = timeline.DeleteBetween(timeline.Markers[1].Id, timeline.Markers[2].Id).OkValue;
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
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[3].Id).OkValue;
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del.Id, 1.0).OkValue;

        // Restore middle 20-30 → two deleted runs (10-20 and 30-40)
        timeline = timeline.RestoreBetween(timeline.Markers[1].Id, timeline.Markers[2].Id).OkValue;
        timeline.Joins.Should().HaveCount(2);
        timeline.Joins.Should().OnlyContain(j => j.Mode == TransitionMode.HardCut);
    }

    [Fact]
    public void SetJoinCrossfadeFailsWhenOverCap()
    {
        var timeline = CreateWithMarkers(5, 10);
        // keep 0-5 (5s), delete 5-10, keep 10-100 (90s) → cap = 5
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).OkValue;
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);

        var fail = timeline.SetJoinCrossfade(del.Id, 6.0);
        fail.IsSuccess.Should().BeFalse();
        fail.Error!.Value.Code.Should().Be("CrossfadeOutOfRange");

        var ok = timeline.SetJoinCrossfade(del.Id, 5.0);
        ok.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void LeadingDeleteHasNoJoin()
    {
        var timeline = CreateWithMarkers(10, 20);
        timeline = timeline.DeleteBetween(Timeline.StartBoundId, timeline.Markers[0].Id).OkValue;
        timeline.Joins.Should().BeEmpty();
        timeline.GetDeletedRuns().Single().IsInterior.Should().BeFalse();
    }

    [Fact]
    public void EdgeDeletedRunsHaveStableIds()
    {
        var timeline = CreateWithMarkers(10, 20, 30);
        timeline = timeline.DeleteBetween(Timeline.StartBoundId, timeline.Markers[0].Id).OkValue;
        timeline = timeline.DeleteBetween(timeline.Markers[2].Id, Timeline.EndBoundId).OkValue;

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
        timeline = timeline.DeleteBetween(Timeline.StartBoundId, timeline.Markers[0].Id).OkValue;
        timeline = timeline.DeleteBetween(timeline.Markers[2].Id, Timeline.EndBoundId).OkValue;

        timeline = timeline.RestoreDeletedRun(Timeline.LeadingDeletedRunId).OkValue;
        timeline.GetDeletedRuns().Should().ContainSingle(r => r.Id == Timeline.TrailingDeletedRunId);

        timeline = timeline.RestoreDeletedRun(Timeline.TrailingDeletedRunId).OkValue;
        timeline.GetDeletedRuns().Should().BeEmpty();
        timeline.Segments.Should().OnlyContain(s => !s.IsDeleted);
    }

    [Fact]
    public void TryBuildExportPlanFailsWhenEverythingDeleted()
    {
        var timeline = CreateWithMarkers(10);
        timeline = timeline.DeleteBetween(Timeline.StartBoundId, Timeline.EndBoundId).OkValue;
        var plan = timeline.TryBuildExportPlan();
        plan.IsSuccess.Should().BeFalse();
        plan.Error!.Value.Code.Should().Be("NothingToExport");
    }

    [Fact]
    public void TryBuildExportPlanSucceedsWithJoins()
    {
        var timeline = CreateWithMarkers(10, 20, 30);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).OkValue;
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del.Id, 1.5).OkValue;

        var plan = timeline.TryBuildExportPlan();
        plan.IsSuccess.Should().BeTrue();
        plan.OkValue.Clips.Should().HaveCount(2);
        plan.OkValue.JoinsBetweenClips.Should().ContainSingle()
            .Which.Mode.Should().Be(TransitionMode.Crossfade);
        plan.OkValue.JoinsBetweenClips[0].CrossfadeSeconds.Should().Be(1.5);
    }

    [Fact]
    public void SetEndFadeFailsWhenTooLong()
    {
        var timeline = CreateWithMarkers(90);
        // final keep after deleting 0-90 would be 10s; without delete final is 100
        var result = timeline.SetEndFade(true, 101);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void OperationsDoNotThrowOnInvalidInput()
    {
        var timeline = Timeline.Create(TimeSpan.FromSeconds(10));
        var act = () => timeline.RemoveMarker(Guid.NewGuid());
        act.Should().NotThrow();
        act().IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void StructuralSharingUnaffectedMarkersPreserved()
    {
        var timeline = CreateWithMarkers(10, 20, 30);
        var marker1 = timeline.Markers[0];
        var marker3 = timeline.Markers[2];

        // Delete middle segment; flanking markers stay shared instances.
        var next = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).OkValue;
        ReferenceEquals(next.Markers[0], marker1).Should().BeTrue();
        ReferenceEquals(next.Markers[2], marker3).Should().BeTrue();
    }

    [Fact]
    public void ProjectDocumentRoundTrips()
    {
        var timeline = CreateWithMarkers(10, 20, 30);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).OkValue;
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del.Id, 1.0).OkValue;
        timeline = timeline.SetEndFade(true, 2.0).OkValue;

        var doc = ProjectDocument.FromTimeline(@"C:\video.mp4", timeline);
        var json = ProjectDocument.Serialize(doc);
        var loaded = ProjectDocument.Deserialize(json).OkValue.ToTimeline();
        loaded.IsSuccess.Should().BeTrue();
        loaded.OkValue.Timeline.Joins.Should().ContainSingle(j => j.Mode == TransitionMode.Crossfade);
        loaded.OkValue.Timeline.Settings.FadeToBlackAtEnd.Should().BeTrue();
        loaded.OkValue.Timeline.Settings.FadeFromBlackAtStart.Should().BeFalse();
    }

    [Fact]
    public void SetStartFadeFailsWhenTooLong()
    {
        var timeline = CreateWithMarkers(90);
        var result = timeline.SetStartFade(true, 101);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void SnapToNearestKeptSnapsDeletedTimesToEdges()
    {
        var timeline = CreateWithMarkers(10, 20, 40, 50);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).OkValue;
        timeline = timeline.DeleteBetween(timeline.Markers[2].Id, timeline.Markers[3].Id).OkValue;

        timeline.IsKeptTime(TimeSpan.FromSeconds(5)).Should().BeTrue();
        timeline.IsKeptTime(TimeSpan.FromSeconds(15)).Should().BeFalse();
        timeline.SnapToNearestKept(TimeSpan.FromSeconds(15)).Should().Be(TimeSpan.FromSeconds(10));
        timeline.SnapToNearestKept(TimeSpan.FromSeconds(18)).Should().Be(TimeSpan.FromSeconds(20));
        timeline.SnapToNearestKept(TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ProjectDocumentRoundTripsStartFade()
    {
        var timeline = CreateWithMarkers(10, 20, 30);
        timeline = timeline.SetStartFade(true, 1.5).OkValue;
        timeline = timeline.SetEndFade(true, 2.0).OkValue;

        var doc = ProjectDocument.FromTimeline(@"C:\video.mp4", timeline);
        var loaded = ProjectDocument.Deserialize(ProjectDocument.Serialize(doc)).OkValue.ToTimeline();
        loaded.IsSuccess.Should().BeTrue();
        loaded.OkValue.Timeline.Settings.FadeFromBlackAtStart.Should().BeTrue();
        loaded.OkValue.Timeline.Settings.FadeFromBlackSeconds.Should().Be(1.5);
        loaded.OkValue.Timeline.Settings.FadeToBlackAtEnd.Should().BeTrue();
        loaded.OkValue.Timeline.Settings.FadeToBlackSeconds.Should().Be(2.0);
    }
}
