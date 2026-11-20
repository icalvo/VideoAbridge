using System.Collections.Immutable;

namespace VideoAbridge.Core.Versions;

/// <summary>
/// Per-video set of named abridged versions with shared scene-detect markers.
/// </summary>
public sealed class AbridgedVersionSet
{
    public const string DefaultVersionName = "Default";

    private AbridgedVersionSet(
        TimeSpan duration,
        TimeSpan sceneDetectPosition,
        Guid activeVersionId,
        ImmutableList<AbridgedVersion> versions)
    {
        Duration = duration;
        SceneDetectPosition = sceneDetectPosition < TimeSpan.Zero ? TimeSpan.Zero : sceneDetectPosition;
        ActiveVersionId = activeVersionId;
        Versions = versions;
    }

    public TimeSpan Duration { get; }
    public TimeSpan SceneDetectPosition { get; }
    public Guid ActiveVersionId { get; }
    public ImmutableList<AbridgedVersion> Versions { get; }

    public AbridgedVersion Active =>
        Versions.First(v => v.Id == ActiveVersionId);

    public Timeline ActiveTimeline => Active.Timeline;

    public static Result<AbridgedVersionSet> CreateDefault(TimeSpan duration) =>
        Timeline.Create(duration).Map(timeline =>
        {
            var id = Guid.NewGuid();
            var version = new AbridgedVersion(id, DefaultVersionName, timeline);
            return new AbridgedVersionSet(duration, TimeSpan.Zero, id, [version]);
        });

    public static Result<AbridgedVersionSet> FromPersisted(
        TimeSpan duration,
        TimeSpan sceneDetectPosition,
        Guid activeVersionId,
        ImmutableList<AbridgedVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        if (duration <= TimeSpan.Zero)
            return Fail<AbridgedVersionSet>("InvalidDuration", "Duration must be positive.");
        if (versions.Count == 0)
            return Fail<AbridgedVersionSet>("VersionsRequired", "At least one version is required.");
        if (versions.All(v => v.Id != activeVersionId))
            return Fail<AbridgedVersionSet>("VersionNotFound", "Active version was not found.");

        return Ok(new AbridgedVersionSet(duration, sceneDetectPosition, activeVersionId, versions));
    }

    public AbridgedVersionSet WithSceneDetectPosition(TimeSpan position) =>
        new(Duration, position, ActiveVersionId, Versions);

    public Result<AbridgedVersionSet> ReplaceActiveTimeline(Timeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.Duration != Duration)
            return Fail<AbridgedVersionSet>("DurationMismatch", "Timeline duration must match the version set.");

        ImmutableList<AbridgedVersion> updated =
        [
            .. Versions.Select(v =>
                v.Id == ActiveVersionId ? v.WithTimeline(timeline) : v)
        ];
        return Ok(new AbridgedVersionSet(Duration, SceneDetectPosition, ActiveVersionId, updated));
    }

    public Result<AbridgedVersionSet> SetActive(Guid versionId)
    {
        if (Versions.All(v => v.Id != versionId))
            return Fail<AbridgedVersionSet>("VersionNotFound", "Version was not found.");

        return Ok(new AbridgedVersionSet(Duration, SceneDetectPosition, versionId, Versions));
    }

    public Result<AbridgedVersionSet> CreateVersionFromScenes(string name) =>
        ValidateNewName(name).Bind(validName =>
        {
            var timeline = BuildScenesOnlyTimeline();
            var id = Guid.NewGuid();
            var version = new AbridgedVersion(id, validName, timeline);
            return Ok(new AbridgedVersionSet(
                Duration,
                SceneDetectPosition,
                id,
                Versions.Add(version)));
        });

    public Result<AbridgedVersionSet> DuplicateActive(string name) =>
        ValidateNewName(name).Bind(validName =>
        {
            var id = Guid.NewGuid();
            // Timeline is immutable; sharing the instance is safe until either side is replaced.
            var version = new AbridgedVersion(id, validName, ActiveTimeline);
            return Ok(new AbridgedVersionSet(
                Duration,
                SceneDetectPosition,
                id,
                Versions.Add(version)));
        });

    public Result<AbridgedVersionSet> Rename(Guid versionId, string name)
    {
        var index = Versions.FindIndex(v => v.Id == versionId);
        if (index < 0)
            return Fail<AbridgedVersionSet>("VersionNotFound", "Version was not found.");

        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
            return Fail<AbridgedVersionSet>("InvalidVersionName", "Version name is required.");

        if (Versions.Any(v =>
                v.Id != versionId
                && string.Equals(v.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail<AbridgedVersionSet>("DuplicateVersionName", "A version with that name already exists.");
        }

        var updated = Versions.SetItem(index, Versions[index].WithName(trimmed));
        return Ok(new AbridgedVersionSet(Duration, SceneDetectPosition, ActiveVersionId, updated));
    }

    public Result<AbridgedVersionSet> RenameActive(string name) => Rename(ActiveVersionId, name);

    /// <summary>Moves a version to <paramref name="newIndex"/> (0-based) within the set.</summary>
    public Result<AbridgedVersionSet> Reorder(Guid versionId, int newIndex)
    {
        if (newIndex < 0 || newIndex >= Versions.Count)
            return Fail<AbridgedVersionSet>("InvalidVersionIndex", "Version index is out of range.");

        var index = Versions.FindIndex(v => v.Id == versionId);
        if (index < 0)
            return Fail<AbridgedVersionSet>("VersionNotFound", "Version was not found.");

        if (index == newIndex)
            return Ok(this);

        var version = Versions[index];
        var updated = Versions.RemoveAt(index).Insert(newIndex, version);
        return Ok(new AbridgedVersionSet(Duration, SceneDetectPosition, ActiveVersionId, updated));
    }

    public Result<AbridgedVersionSet> Remove(Guid versionId)
    {
        if (Versions.Count <= 1)
            return Fail<AbridgedVersionSet>("CannotDeleteLastVersion", "At least one version is required.");

        var index = Versions.FindIndex(v => v.Id == versionId);
        if (index < 0)
            return Fail<AbridgedVersionSet>("VersionNotFound", "Version was not found.");

        var remaining = Versions.RemoveAt(index);
        var activeId = ActiveVersionId;
        if (activeId == versionId)
        {
            var neighborIndex = Math.Min(index, remaining.Count - 1);
            activeId = remaining[neighborIndex].Id;
        }

        return Ok(new AbridgedVersionSet(Duration, SceneDetectPosition, activeId, remaining));
    }

    /// <summary>
    /// Inserts a scene-detect marker with a shared id into every version that accepts it.
    /// </summary>
    public Result<AbridgedVersionSet> AddSharedSceneMarker(TimeSpan time, Guid id)
    {
        var updated = Versions.Aggregate(
            (Versions: ImmutableList<AbridgedVersion>.Empty, AnyAdded: false),
            (state, version) =>
            {
                return version.Timeline.AddMarker(time, MarkerSource.SceneDetect, id).Match(
                    next => (state.Versions.Add(version.WithTimeline(next)), true),
                    _ => (state.Versions.Add(version), state.AnyAdded));
            });

        if (!updated.AnyAdded)
            return Fail<AbridgedVersionSet>("SceneMarkerNotAdded", "Scene marker was not added to any version.");

        var position = time > SceneDetectPosition ? time : SceneDetectPosition;
        return Ok(new AbridgedVersionSet(Duration, position, ActiveVersionId, updated.Versions));
    }

    public ImmutableList<Marker> GetSharedSceneMarkers() =>
    [
        .. ActiveTimeline.Markers
            .Where(m => m.Source == MarkerSource.SceneDetect)
            .OrderBy(m => m.Time)
    ];

    private Timeline BuildScenesOnlyTimeline() =>
        GetSharedSceneMarkers().Aggregate(
            Timeline.Empty(Duration),
            (timeline, scene) =>
                timeline.AddMarker(scene.Time, MarkerSource.SceneDetect, scene.Id)
                    .Match(next => next, _ => timeline));

    private Result<string> ValidateNewName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
            return Fail<string>("InvalidVersionName", "Version name is required.");

        if (Versions.Any(v => string.Equals(v.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            return Fail<string>("DuplicateVersionName", "A version with that name already exists.");

        return Ok(trimmed);
    }
}
