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
            timeline = result.Value!;
        }

        return timeline;
    }

    [Fact]
    public void Create_HasSingleKeptSegment()
    {
        var timeline = Timeline.Create(TimeSpan.FromSeconds(60));
        timeline.Segments.Should().ContainSingle().Which.IsDeleted.Should().BeFalse();
        timeline.Markers.Should().BeEmpty();
        timeline.Joins.Should().BeEmpty();
    }

    [Fact]
    public void AddMarker_SplitsSegment_InheritingState()
    {
        var timeline = CreateWithMarkers(10, 20, 30);
        timeline.Segments.Should().HaveCount(4);

        var mid = timeline.Markers[1];
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[2].Id).Value!;

        var add = timeline.AddMarker(TimeSpan.FromSeconds(25), MarkerSource.User);
        add.IsSuccess.Should().BeTrue();
        var segmentAround = add.Value!.Segments[2];
        segmentAround.IsDeleted.Should().BeTrue();
        add.Value.Joins.Should().HaveCount(1); // still one interior deleted run
    }

    [Fact]
    public void AddMarker_FailsWhenTooClose()
    {
        var timeline = CreateWithMarkers(10);
        var result = timeline.AddMarker(TimeSpan.FromSeconds(10.01), MarkerSource.User);
        result.IsSuccess.Should().BeFalse();
        result.Error!.Value.Code.Should().Be("MarkerTooClose");
    }

    [Fact]
    public void RemoveMarker_FailsOnKeepDeleteBoundary()
    {
        var timeline = CreateWithMarkers(10, 20);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).Value!;

        var result = timeline.RemoveMarker(timeline.Markers[0].Id);
        result.IsSuccess.Should().BeFalse();
        result.Error!.Value.Code.Should().Be("MarkerIsBoundary");
    }

    [Fact]
    public void RemoveMarker_SucceedsWhenBothSidesKept()
    {
        var timeline = CreateWithMarkers(10, 20);
        var result = timeline.RemoveMarker(timeline.Markers[0].Id);
        result.IsSuccess.Should().BeTrue();
        result.Value!.Markers.Should().ContainSingle();
        result.Value.Segments.Should().HaveCount(2);
    }

    [Fact]
    public void DeleteBetween_CreatesInteriorHardCut()
    {
        var timeline = CreateWithMarkers(10, 20, 30);
        var result = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id);
        result.IsSuccess.Should().BeTrue();

        var runs = result.Value!.GetDeletedRuns();
        runs.Should().ContainSingle(r => r.IsInterior);
        runs[0].Join!.Mode.Should().Be(TransitionMode.HardCut);
    }

    [Fact]
    public void CoalesceDeletedRuns_ResetsToHardCut()
    {
        var timeline = CreateWithMarkers(10, 20, 30, 40, 50);
        // Del1 between 10-20 with crossfade, Del2 between 30-40 hard cut, keep 20-30
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).Value!;
        var del1 = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del1.Id, 1.0).Value!;

        timeline = timeline.DeleteBetween(timeline.Markers[2].Id, timeline.Markers[3].Id).Value!;
        timeline.Joins.Should().HaveCount(2);
        timeline.Joins[0].Mode.Should().Be(TransitionMode.Crossfade);

        // Delete keep between them (20-30)
        timeline = timeline.DeleteBetween(timeline.Markers[1].Id, timeline.Markers[2].Id).Value!;
        timeline.Joins.Should().ContainSingle();
        timeline.Joins[0].Mode.Should().Be(TransitionMode.HardCut);
        timeline.Joins[0].CrossfadeSeconds.Should().Be(0);
    }

    [Fact]
    public void ExpandSingleDeletedRun_PreservesCrossfade()
    {
        var timeline = CreateWithMarkers(10, 20, 30, 40);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).Value!;
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del.Id, 2.0).Value!;

        // Expand into next keep segment 20-30
        timeline = timeline.DeleteBetween(timeline.Markers[1].Id, timeline.Markers[2].Id).Value!;
        timeline.Joins.Should().ContainSingle();
        timeline.Joins[0].Mode.Should().Be(TransitionMode.Crossfade);
        timeline.Joins[0].DeletedRunId.Should().Be(del.Id);
        timeline.Joins[0].CrossfadeSeconds.Should().Be(2.0);
    }

    [Fact]
    public void SplitDeletedRun_BothBecomeHardCut()
    {
        var timeline = CreateWithMarkers(10, 20, 30, 40);
        // One deleted run covering 10-40
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[3].Id).Value!;
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del.Id, 1.0).Value!;

        // Restore middle 20-30 → two deleted runs (10-20 and 30-40)
        timeline = timeline.RestoreBetween(timeline.Markers[1].Id, timeline.Markers[2].Id).Value!;
        timeline.Joins.Should().HaveCount(2);
        timeline.Joins.Should().OnlyContain(j => j.Mode == TransitionMode.HardCut);
    }

    [Fact]
    public void SetJoinCrossfade_FailsWhenOverCap()
    {
        var timeline = CreateWithMarkers(5, 10);
        // keep 0-5 (5s), delete 5-10, keep 10-100 (90s) → cap = 5
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).Value!;
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);

        var fail = timeline.SetJoinCrossfade(del.Id, 6.0);
        fail.IsSuccess.Should().BeFalse();
        fail.Error!.Value.Code.Should().Be("CrossfadeOutOfRange");

        var ok = timeline.SetJoinCrossfade(del.Id, 5.0);
        ok.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void LeadingDelete_HasNoJoin()
    {
        var timeline = CreateWithMarkers(10, 20);
        timeline = timeline.DeleteBetween(Timeline.StartBoundId, timeline.Markers[0].Id).Value!;
        timeline.Joins.Should().BeEmpty();
        timeline.GetDeletedRuns().Single().IsInterior.Should().BeFalse();
    }

    [Fact]
    public void MoveMarker_ReclampsCrossfade()
    {
        var timeline = CreateWithMarkers(10, 30);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).Value!;
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del.Id, 8.0).Value!; // cap was min(10,70)=10

        // Move first marker to 3s → left keep becomes 3s → clamp to 3
        var moved = timeline.MoveMarker(timeline.Markers[0].Id, TimeSpan.FromSeconds(3));
        moved.IsSuccess.Should().BeTrue();
        moved.Value!.Joins[0].CrossfadeSeconds.Should().Be(3.0);
    }

    [Fact]
    public void TryBuildExportPlan_FailsWhenEverythingDeleted()
    {
        var timeline = CreateWithMarkers(10);
        timeline = timeline.DeleteBetween(Timeline.StartBoundId, Timeline.EndBoundId).Value!;
        var plan = timeline.TryBuildExportPlan();
        plan.IsSuccess.Should().BeFalse();
        plan.Error!.Value.Code.Should().Be("NothingToExport");
    }

    [Fact]
    public void TryBuildExportPlan_SucceedsWithJoins()
    {
        var timeline = CreateWithMarkers(10, 20, 30);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).Value!;
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del.Id, 1.5).Value!;

        var plan = timeline.TryBuildExportPlan();
        plan.IsSuccess.Should().BeTrue();
        plan.Value!.Clips.Should().HaveCount(2);
        plan.Value.JoinsBetweenClips.Should().ContainSingle()
            .Which.Mode.Should().Be(TransitionMode.Crossfade);
        plan.Value.JoinsBetweenClips[0].CrossfadeSeconds.Should().Be(1.5);
    }

    [Fact]
    public void SetEndFade_FailsWhenTooLong()
    {
        var timeline = CreateWithMarkers(90);
        // final keep after deleting 0-90 would be 10s; without delete final is 100
        var result = timeline.SetEndFade(true, 101);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Operations_DoNotThrow_OnInvalidInput()
    {
        var timeline = Timeline.Create(TimeSpan.FromSeconds(10));
        var act = () => timeline.RemoveMarker(Guid.NewGuid());
        act.Should().NotThrow();
        act().IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void StructuralSharing_UnaffectedMarkersPreserved()
    {
        var timeline = CreateWithMarkers(10, 20, 30);
        var marker1 = timeline.Markers[0];
        var marker3 = timeline.Markers[2];

        var moved = timeline.MoveMarker(timeline.Markers[1].Id, TimeSpan.FromSeconds(22)).Value!;
        ReferenceEquals(moved.Markers[0], marker1).Should().BeTrue();
        ReferenceEquals(moved.Markers[2], marker3).Should().BeTrue();
    }

    [Fact]
    public void ProjectDocument_RoundTrips()
    {
        var timeline = CreateWithMarkers(10, 20, 30);
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).Value!;
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del.Id, 1.0).Value!;
        timeline = timeline.SetEndFade(true, 2.0).Value!;

        var doc = ProjectDocument.FromTimeline(@"C:\video.mp4", timeline);
        var json = ProjectDocument.Serialize(doc);
        var loaded = ProjectDocument.Deserialize(json).Value!.ToTimeline();
        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.Timeline.Joins.Should().ContainSingle(j => j.Mode == TransitionMode.Crossfade);
        loaded.Value.Timeline.Settings.FadeToBlackAtEnd.Should().BeTrue();
    }
}
