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

    [Fact]
    public void RoundTripsMarkerLabels()
    {
        var timeline = Timeline.Create(TimeSpan.FromSeconds(100)).RequireOk();
        timeline = timeline.AddMarker(TimeSpan.FromSeconds(10), MarkerSource.User).RequireOk();
        timeline = timeline.AddMarker(TimeSpan.FromSeconds(20), MarkerSource.User).RequireOk();
        timeline = timeline.SetMarkerLabel(timeline.Markers[0].Id, "Intro").RequireOk();

        var doc = ProjectDocument.FromTimeline(@"C:\video.mp4", timeline);
        var json = ProjectDocument.Serialize(doc);
        json.Should().Contain("\"label\": \"Intro\"");
        json.Should().NotContain("\"label\": null");

        var loaded = ProjectDocument.Deserialize(json).RequireOk().ToTimeline().RequireOk();
        loaded.Timeline.Markers[0].Label.Should().Be("Intro");
        loaded.Timeline.Markers[1].Label.Should().BeEmpty();
    }

    [Fact]
    public void MissingMarkerLabelDeserializesAsEmpty()
    {
        const string json =
            """
            {
              "videoPath": "C:\\video.mp4",
              "durationSeconds": 100,
              "markers": [
                { "id": "11111111-1111-1111-1111-111111111111", "timeSeconds": 10, "source": "User" }
              ],
              "segmentDeleted": [ false, false ]
            }
            """;

        var loaded = ProjectDocument.Deserialize(json).RequireOk().ToTimeline().RequireOk();
        loaded.Timeline.Markers.Should().ContainSingle()
            .Which.Label.Should().BeEmpty();
    }
}
