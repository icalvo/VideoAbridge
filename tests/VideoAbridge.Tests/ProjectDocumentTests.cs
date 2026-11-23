using FluentAssertions;
using VideoAbridge.Services;

namespace VideoAbridge.Tests;

public class ProjectDocumentTests
{
    [Fact]
    public void RoundTripsJoinsAndEndFade()
    {
        var timeline = Timeline.Create(TimeSpan.FromSeconds(100)).RequireOk();
        timeline = timeline.AddMarker(TimeSpan.FromSeconds(10), MarkerSource.User).RequireOk();
        timeline = timeline.AddMarker(TimeSpan.FromSeconds(20), MarkerSource.User).RequireOk();
        timeline = timeline.AddMarker(TimeSpan.FromSeconds(30), MarkerSource.User).RequireOk();
        timeline = timeline.DeleteBetween(timeline.Markers[0].Id, timeline.Markers[1].Id).RequireOk();
        var del = timeline.GetDeletedRuns().Single(r => r.IsInterior);
        timeline = timeline.SetJoinCrossfade(del.Id, TimeSpan.FromSeconds(1.0)).RequireOk();
        timeline = timeline.SetEndFade(2.0).RequireOk();

        var doc = ProjectDocument.FromTimeline(@"C:\video.mp4", timeline);
        var json = ProjectDocument.Serialize(doc);
        var loaded = ProjectDocument.Deserialize(json).RequireOk().ToTimeline().RequireOk();
        loaded.Timeline.Joins.Should().ContainSingle(j => j.Mode == TransitionMode.Crossfade);
        loaded.Timeline.Settings.FadeToBlackSeconds.Should().Be(2.0);
        loaded.Timeline.Settings.FadeFromBlackSeconds.Should().BeNull();
    }

    [Fact]
    public void RoundTripsStartFade()
    {
        var timeline = Timeline.Create(TimeSpan.FromSeconds(100)).RequireOk();
        timeline = timeline.AddMarker(TimeSpan.FromSeconds(10), MarkerSource.User).RequireOk();
        timeline = timeline.AddMarker(TimeSpan.FromSeconds(20), MarkerSource.User).RequireOk();
        timeline = timeline.AddMarker(TimeSpan.FromSeconds(30), MarkerSource.User).RequireOk();
        timeline = timeline.SetStartFade(1.5).RequireOk();
        timeline = timeline.SetEndFade(2.0).RequireOk();

        var doc = ProjectDocument.FromTimeline(@"C:\video.mp4", timeline);
        var loaded = ProjectDocument.Deserialize(ProjectDocument.Serialize(doc)).RequireOk().ToTimeline().RequireOk();
        loaded.Timeline.Settings.FadeFromBlackSeconds.Should().Be(1.5);
        loaded.Timeline.Settings.FadeToBlackSeconds.Should().Be(2.0);
    }
}
