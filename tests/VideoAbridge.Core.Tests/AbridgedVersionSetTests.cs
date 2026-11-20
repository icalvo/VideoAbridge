using FluentAssertions;

namespace VideoAbridge.Core.Tests;

public class AbridgedVersionSetTests
{
    [Fact]
    public void CreateDefaultHasSingleDefaultVersion()
    {
        var set = AbridgedVersionSet.CreateDefault(TimeSpan.FromSeconds(60)).RequireOk();
        set.Versions.Should().ContainSingle();
        set.Active.Name.Should().Be(AbridgedVersionSet.DefaultVersionName);
        set.ActiveTimeline.Markers.Should().BeEmpty();
    }

    [Fact]
    public void CreateDefaultRejectsNonPositiveDuration()
    {
        var error = AbridgedVersionSet.CreateDefault(TimeSpan.Zero).RequireError();
        error.Code.Should().Be("InvalidDuration");
    }

    [Fact]
    public void RemoveMarkerProtectsSceneDetectMarkers()
    {
        var timeline = Timeline.Create(TimeSpan.FromSeconds(60)).RequireOk();
        timeline = timeline.AddMarker(TimeSpan.FromSeconds(10), MarkerSource.SceneDetect).RequireOk();
        var error = timeline.RemoveMarker(timeline.Markers[0].Id).RequireError();
        error.Code.Should().Be("SceneMarkerProtected");
    }

    [Fact]
    public void CreateVersionFromScenesCopiesSharedSceneMarkersOnly()
    {
        var set = AbridgedVersionSet.CreateDefault(TimeSpan.FromSeconds(100)).RequireOk();
        var sceneId = Guid.NewGuid();
        set = set.AddSharedSceneMarker(TimeSpan.FromSeconds(20), sceneId).RequireOk();
        set = set.ReplaceActiveTimeline(
            set.ActiveTimeline.AddMarker(TimeSpan.FromSeconds(40), MarkerSource.User).RequireOk()).RequireOk();
        set = set.ReplaceActiveTimeline(
            set.ActiveTimeline.DeleteBetween(sceneId, set.ActiveTimeline.Markers[^1].Id).RequireOk()).RequireOk();

        var created = set.CreateVersionFromScenes("Alt").RequireOk();
        created.Active.Name.Should().Be("Alt");
        created.ActiveTimeline.Markers.Should().ContainSingle();
        created.ActiveTimeline.Markers[0].Id.Should().Be(sceneId);
        created.ActiveTimeline.Markers[0].Source.Should().Be(MarkerSource.SceneDetect);
        created.ActiveTimeline.Segments.Should().OnlyContain(s => !s.IsDeleted);
        created.Versions.Should().HaveCount(2);
    }

    [Fact]
    public void DuplicateActiveClonesTimelineAndActivates()
    {
        var set = AbridgedVersionSet.CreateDefault(TimeSpan.FromSeconds(60)).RequireOk();
        set = set.ReplaceActiveTimeline(
            set.ActiveTimeline.AddMarker(TimeSpan.FromSeconds(15), MarkerSource.User).RequireOk()).RequireOk();

        var dup = set.DuplicateActive("Copy").RequireOk();
        dup.Active.Name.Should().Be("Copy");
        dup.Active.Id.Should().NotBe(set.ActiveVersionId);
        dup.ActiveTimeline.Markers.Should().ContainSingle(m => m.Source == MarkerSource.User);
        dup.Versions.Should().HaveCount(2);
    }

    [Fact]
    public void ReplaceActiveTimelineRejectsDurationMismatch()
    {
        var set = AbridgedVersionSet.CreateDefault(TimeSpan.FromSeconds(60)).RequireOk();
        var other = Timeline.Create(TimeSpan.FromSeconds(30)).RequireOk();
        var error = set.ReplaceActiveTimeline(other).RequireError();
        error.Code.Should().Be("DurationMismatch");
    }

    [Fact]
    public void CreateVersionRejectsDuplicateNamesCaseInsensitive()
    {
        var set = AbridgedVersionSet.CreateDefault(TimeSpan.FromSeconds(30)).RequireOk();
        var error = set.CreateVersionFromScenes("default").RequireError();
        error.Code.Should().Be("DuplicateVersionName");
    }

    [Fact]
    public void RenameEnforcesUniqueness()
    {
        var set = AbridgedVersionSet.CreateDefault(TimeSpan.FromSeconds(30)).RequireOk();
        set = set.CreateVersionFromScenes("Alt").RequireOk();
        var error = set.RenameActive("Default").RequireError();
        error.Code.Should().Be("DuplicateVersionName");
    }

    [Fact]
    public void RemoveLastVersionFails()
    {
        var set = AbridgedVersionSet.CreateDefault(TimeSpan.FromSeconds(30)).RequireOk();
        var error = set.Remove(set.ActiveVersionId).RequireError();
        error.Code.Should().Be("CannotDeleteLastVersion");
    }

    [Fact]
    public void RemoveActiveSelectsNeighbor()
    {
        var set = AbridgedVersionSet.CreateDefault(TimeSpan.FromSeconds(30)).RequireOk();
        var firstId = set.ActiveVersionId;
        set = set.CreateVersionFromScenes("Two").RequireOk();
        var secondId = set.ActiveVersionId;
        set = set.CreateVersionFromScenes("Three").RequireOk();

        set = set.SetActive(secondId).RequireOk();
        set = set.Remove(secondId).RequireOk();
        set.Versions.Select(v => v.Id).Should().Equal(firstId, set.Versions[^1].Id);
        set.ActiveVersionId.Should().Be(set.Versions[^1].Id);
    }

    [Fact]
    public void AddSharedSceneMarkerUpdatesAllVersionsWithSameId()
    {
        var set = AbridgedVersionSet.CreateDefault(TimeSpan.FromSeconds(90)).RequireOk();
        set = set.CreateVersionFromScenes("B").RequireOk();
        var id = Guid.NewGuid();
        set = set.AddSharedSceneMarker(TimeSpan.FromSeconds(12), id).RequireOk();

        set.Versions.Should().HaveCount(2);
        foreach (var version in set.Versions)
        {
            version.Timeline.Markers.Should().ContainSingle(m => m.Id == id && m.Source == MarkerSource.SceneDetect);
        }

        set.SceneDetectPosition.Should().Be(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public void ReorderMovesVersionAndKeepsActive()
    {
        var set = AbridgedVersionSet.CreateDefault(TimeSpan.FromSeconds(30)).RequireOk();
        var firstId = set.ActiveVersionId;
        set = set.CreateVersionFromScenes("Two").RequireOk();
        var secondId = set.ActiveVersionId;
        set = set.CreateVersionFromScenes("Three").RequireOk();
        var thirdId = set.ActiveVersionId;

        set = set.Reorder(thirdId, 0).RequireOk();
        set.Versions.Select(v => v.Id).Should().Equal(thirdId, firstId, secondId);
        set.ActiveVersionId.Should().Be(thirdId);
    }

    [Fact]
    public void ReorderRejectsOutOfRangeIndex()
    {
        var set = AbridgedVersionSet.CreateDefault(TimeSpan.FromSeconds(30)).RequireOk();
        var error = set.Reorder(set.ActiveVersionId, 1).RequireError();
        error.Code.Should().Be("InvalidVersionIndex");
    }
}
