using System.Collections.Immutable;

namespace VideoAbridge.Core.Editing;

public sealed class Timeline
{
    public static readonly Guid StartBoundId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid EndBoundId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    /// <summary>Stable id for a deleted run that starts at t=0 (not interior).</summary>
    public static readonly Guid LeadingDeletedRunId = Guid.Parse("00000000-0000-0000-0000-000000000003");
    /// <summary>Stable id for a deleted run that ends at Duration (not interior).</summary>
    public static readonly Guid TrailingDeletedRunId = Guid.Parse("00000000-0000-0000-0000-000000000004");
    /// <summary>Stable id when the entire timeline is deleted.</summary>
    public static readonly Guid FullDeletedRunId = Guid.Parse("00000000-0000-0000-0000-000000000005");
    public static readonly TimeSpan MarkerEpsilon = TimeSpan.FromMilliseconds(40);

    private Timeline(
        TimeSpan duration,
        ImmutableList<Marker> markers,
        ImmutableList<SegmentState> segments,
        ImmutableList<JoinTransition> joins,
        ProjectSettings settings)
    {
        Duration = duration;
        Markers = markers;
        Segments = segments;
        Joins = joins;
        Settings = settings;
    }

    public TimeSpan Duration { get; }
    public ImmutableList<Marker> Markers { get; }
    public ImmutableList<SegmentState> Segments { get; }

    /// <summary>
    /// Join transitions for interior deleted runs, ordered left-to-right on the timeline.
    /// </summary>
    public ImmutableList<JoinTransition> Joins { get; }

    public ProjectSettings Settings { get; }

    public static Result<Timeline> Create(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return Fail<Timeline>("InvalidDuration", "Duration must be positive.");

        return Ok(Empty(duration));
    }

    /// <summary>
    /// Builds a timeline from persisted state without applying stepwise topology rules.
    /// </summary>
    public static Result<Timeline> FromPersistedState(
        TimeSpan duration,
        ImmutableList<Marker> markers,
        ImmutableList<bool> segmentDeleted,
        ImmutableList<JoinTransition> joins,
        ProjectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(segmentDeleted);
        ArgumentNullException.ThrowIfNull(joins);
        ArgumentNullException.ThrowIfNull(settings);
        if (duration <= TimeSpan.Zero)
            return Fail<Timeline>("InvalidDuration", "Duration must be positive.");
        if (segmentDeleted.Count != markers.Count + 1)
            return Fail<Timeline>("InvalidSegmentCount", "Segment count must equal marker count + 1.");
        if (markers.Any(m => m.Time <= TimeSpan.Zero || m.Time >= duration))
            return Fail<Timeline>("MarkerOutOfRange", "Marker time must be strictly inside the timeline.");

        ImmutableList<Marker> orderedMarkers = [.. markers.OrderBy(m => m.Time)];
        ImmutableList<SegmentState> segments = [.. segmentDeleted.Select(d => new SegmentState(d))];
        return Ok(new Timeline(duration, orderedMarkers, segments, joins, settings));
    }

    /// <summary>Empty kept timeline; caller must ensure <paramref name="duration"/> is positive.</summary>
    internal static Timeline Empty(TimeSpan duration) =>
        new(
            duration,
            [],
            [new SegmentState(IsDeleted: false)],
            [],
            ProjectSettings.Default);

    public ImmutableList<TimeSpan> GetBoundaries() =>
        [TimeSpan.Zero, .. Markers.Select(m => m.Time), Duration];

    public ImmutableList<DeletedRun> GetDeletedRuns() =>
        CollectDeletedRanges().Aggregate(
            (Runs: ImmutableList<DeletedRun>.Empty, InteriorIndex: 0),
            (state, range) =>
            {
                if (!range.IsInteriorOf(Duration))
                {
                    var edgeId = range.Covers(Duration)
                        ? FullDeletedRunId
                        : range.TouchesStart
                            ? LeadingDeletedRunId
                            : TrailingDeletedRunId;
                    return (state.Runs.Add(new DeletedRun(edgeId, range, false, null)), state.InteriorIndex);
                }

                var join = state.InteriorIndex < Joins.Count
                    ? Joins[state.InteriorIndex]
                    : new JoinTransition(Guid.NewGuid(), TransitionMode.HardCut, 0);
                return (
                    state.Runs.Add(new DeletedRun(join.DeletedRunId, range, true, join)),
                    state.InteriorIndex + 1);
            }).Runs;

    public ImmutableList<TimeRange> GetKeptRuns() =>
        CollectContiguousSegmentRanges(deleted: false);

    /// <summary>Total duration of all non-deleted sections (the abridged output length).</summary>
    public TimeSpan GetAbridgedDuration() =>
        GetKeptRuns().Aggregate(TimeSpan.Zero, (total, run) => total + run.Duration);

    /// <summary>
    /// True when <paramref name="time"/> falls inside a kept section (inclusive of both ends).
    /// </summary>
    public bool IsKeptTime(TimeSpan time) =>
        time >= TimeSpan.Zero
        && time <= Duration
        && GetKeptRuns().Any(run => run.Contains(time));

    /// <summary>
    /// Snaps <paramref name="time"/> to itself when kept, otherwise to the nearest kept edge.
    /// Returns <see cref="TimeSpan.Zero"/> when nothing is kept.
    /// </summary>
    public TimeSpan SnapToNearestKept(TimeSpan time)
    {
        var kept = GetKeptRuns();
        if (kept.Count == 0)
            return TimeSpan.Zero;

        if (time < TimeSpan.Zero)
            time = TimeSpan.Zero;
        else if (time > Duration)
            time = Duration;

        if (kept.Any(run => run.Contains(time)))
            return time;

        return kept.Aggregate(
            kept[0].NearestEdge(time),
            (best, run) =>
            {
                var candidate = run.NearestEdge(time);
                return AbsDiff(time, candidate) < AbsDiff(time, best) ? candidate : best;
            });
    }

    public Result<Timeline> AddMarker(TimeSpan time, MarkerSource source) =>
        AddMarker(time, source, Guid.NewGuid());

    public Result<Timeline> AddMarker(TimeSpan time, MarkerSource source, Guid id)
    {
        if (time <= TimeSpan.Zero || time >= Duration)
            return Fail<Timeline>("MarkerOutOfRange", "Marker time must be strictly inside the timeline.");

        if (Markers.Any(m => AbsDiff(m.Time, time) <= MarkerEpsilon))
            return Fail<Timeline>("MarkerTooClose", "A marker already exists near that time.");

        var insertIndex = Markers.FindIndex(m => m.Time > time);
        if (insertIndex < 0)
            insertIndex = Markers.Count;

        var segmentIndex = insertIndex;
        var oldState = Segments[segmentIndex];
        var newSegments = Segments
            .RemoveAt(segmentIndex)
            .Insert(segmentIndex, oldState)
            .Insert(segmentIndex + 1, oldState);

        var newMarkers = Markers.Insert(insertIndex, new Marker(id, time, source));
        // Splitting inside a run does not change deleted-run topology / joins.
        return Ok(new Timeline(Duration, newMarkers, newSegments, Joins, Settings));
    }

    /// <summary>
    /// Whether <see cref="RemoveMarker"/> would succeed for this id (not scene-detect, not a keep/delete boundary).
    /// </summary>
    public bool CanRemoveMarker(Guid markerId) => GetRemoveMarkerBlockReason(markerId) is null;

    /// <summary>Human-readable reason <see cref="RemoveMarker"/> would fail, or null if it would succeed.</summary>
    public string? GetRemoveMarkerBlockReason(Guid markerId)
    {
        var index = Markers.FindIndex(m => m.Id == markerId);
        if (index < 0)
            return "Marker was not found.";
        if (Markers[index].Source == MarkerSource.SceneDetect)
            return "Scene-detect markers cannot be deleted.";
        if (Segments[index].IsDeleted != Segments[index + 1].IsDeleted)
            return "Cannot delete a marker that separates keep and delete segments.";
        return null;
    }

    public Result<Timeline> RemoveMarker(Guid markerId)
    {
        var index = Markers.FindIndex(m => m.Id == markerId);
        if (index < 0)
            return Fail<Timeline>("MarkerNotFound", "Marker was not found.");

        if (Markers[index].Source == MarkerSource.SceneDetect)
        {
            return Fail<Timeline>(
                "SceneMarkerProtected",
                "Scene-detect markers cannot be deleted.");
        }

        if (Segments[index].IsDeleted != Segments[index + 1].IsDeleted)
        {
            return Fail<Timeline>(
                "MarkerIsBoundary",
                "Cannot remove a marker that separates keep and delete segments.");
        }

        var oldRuns = SnapshotInteriorRuns();
        var newMarkers = Markers.RemoveAt(index);
        var newSegments = Segments.RemoveAt(index + 1);
        return Ok(Rebuild(Duration, newMarkers, newSegments, oldRuns, Settings));
    }

    public Result<Timeline> DeleteBetween(Guid boundaryIdA, Guid boundaryIdB) =>
        ApplyDeletionFlags(boundaryIdA, boundaryIdB, deleted: true);

    public Result<Timeline> RestoreBetween(Guid boundaryIdA, Guid boundaryIdB) =>
        ApplyDeletionFlags(boundaryIdA, boundaryIdB, deleted: false);

    /// <summary>
    /// True when the open range between the boundaries contains at least one segment whose
    /// deleted flag equals <paramref name="isDeleted"/>.
    /// </summary>
    public bool RangeContainsSegment(Guid boundaryIdA, Guid boundaryIdB, bool isDeleted)
    {
        if (!TryGetBoundaryTime(boundaryIdA, out var timeA)
            || !TryGetBoundaryTime(boundaryIdB, out var timeB)
            || timeA >= timeB)
        {
            return false;
        }

        var boundaries = GetBoundaries();
        for (var i = 0; i < Segments.Count; i++)
        {
            var segStart = boundaries[i];
            var segEnd = boundaries[i + 1];
            if (segStart >= timeA && segEnd <= timeB && Segments[i].IsDeleted == isDeleted)
                return true;
        }

        return false;
    }

    public Result<Timeline> RestoreDeletedRun(Guid deletedRunId)
    {
        var run = GetDeletedRuns().FirstOrDefault(r => r.Id == deletedRunId);
        if (run is null)
            return Fail<Timeline>("DeletedRunNotFound", "Deleted run was not found.");

        if (!TryGetBoundaryIdAt(run.Range.Start, out var left)
            || !TryGetBoundaryIdAt(run.Range.End, out var right))
            return Fail<Timeline>("BoundaryNotFound", "Could not resolve deleted run boundaries.");

        return RestoreBetween(left, right);
    }

    public Result<Timeline> SetJoinHardCut(Guid deletedRunId)
    {
        var index = Joins.FindIndex(j => j.DeletedRunId == deletedRunId);
        if (index < 0)
            return Fail<Timeline>("JoinNotFound", "Deleted run join was not found.");

        var updated = Joins[index] with { Mode = TransitionMode.HardCut, CrossfadeSeconds = 0 };
        return Ok(new Timeline(
            Duration, Markers, Segments, Joins.SetItem(index, updated), Settings));
    }

    public Result<Timeline> SetJoinCrossfade(Guid deletedRunId, double seconds)
    {
        var index = Joins.FindIndex(j => j.DeletedRunId == deletedRunId);
        if (index < 0)
            return Fail<Timeline>("JoinNotFound", "Deleted run join was not found.");

        var run = GetDeletedRuns().FirstOrDefault(r => r.Id == deletedRunId);
        if (run is null || !run.IsInterior)
            return Fail<Timeline>("JoinNotInterior", "Crossfade requires an interior deleted run.");

        var cap = GetCrossfadeCap(run.Range);
        if (cap <= 0)
            return Fail<Timeline>("CrossfadeCapZero", "Adjacent kept sections are too short for a crossfade.");

        if (seconds <= 0 || seconds > cap + 1e-9)
        {
            return Fail<Timeline>(
                "CrossfadeOutOfRange",
                $"Crossfade must be in (0, {cap:0.###}] seconds.");
        }

        var updated = Joins[index] with { Mode = TransitionMode.Crossfade, CrossfadeSeconds = seconds };
        return Ok(new Timeline(
            Duration, Markers, Segments, Joins.SetItem(index, updated), Settings));
    }

    /// <summary>Sets start fade; <paramref name="seconds"/> null disables fade-from-black.</summary>
    public Result<Timeline> SetStartFade(double? seconds)
    {
        if (seconds is { } value)
        {
            var max = GetStartFadeCap();
            if (value < 0 || value > max + 1e-9)
            {
                return Fail<Timeline>(
                    "StartFadeOutOfRange",
                    $"Start fade must be between 0 and {max:0.###} seconds.");
            }
        }

        var settings = Settings with { FadeFromBlackSeconds = seconds };
        return Ok(new Timeline(Duration, Markers, Segments, Joins, settings));
    }

    /// <summary>Sets end fade; <paramref name="seconds"/> null disables fade-to-black.</summary>
    public Result<Timeline> SetEndFade(double? seconds)
    {
        if (seconds is { } value)
        {
            var max = GetEndFadeCap();
            if (value < 0 || value > max + 1e-9)
            {
                return Fail<Timeline>(
                    "EndFadeOutOfRange",
                    $"End fade must be between 0 and {max:0.###} seconds.");
            }
        }

        var settings = Settings with { FadeToBlackSeconds = seconds };
        return Ok(new Timeline(Duration, Markers, Segments, Joins, settings));
    }

    public Result<ExportPlan> TryBuildExportPlan()
    {
        var kept = GetKeptRuns();
        if (kept.Count == 0)
            return Fail<ExportPlan>("NothingToExport", "There is no kept material to export.");

        ImmutableList<TimeRange> clips = [.. kept];
        ImmutableList<DeletedRun> deleted = [.. GetDeletedRuns().Where(r => r.IsInterior)];

        if (clips.Count != deleted.Count + 1)
            return Fail<ExportPlan>("InconsistentRuns", "Kept/deleted run topology is inconsistent.");

        var joins = ImmutableList.CreateBuilder<ExportJoin>();
        for (var i = 0; i < deleted.Count; i++)
        {
            var run = deleted[i];
            var join = run.Join ?? new JoinTransition(run.Id, TransitionMode.HardCut, 0);
            if (join.Mode == TransitionMode.Crossfade)
            {
                var cap = Math.Min(clips[i].Duration.TotalSeconds, clips[i + 1].Duration.TotalSeconds);
                if (join.CrossfadeSeconds <= 0 || join.CrossfadeSeconds > cap + 1e-9)
                    return Fail<ExportPlan>("InvalidCrossfade", "A crossfade exceeds the adjacent kept duration cap.");
            }

            joins.Add(new ExportJoin(
                join.Mode,
                join.Mode == TransitionMode.Crossfade ? join.CrossfadeSeconds : 0));
        }

        if (Settings.FadeFromBlackSeconds is { } startFadeSeconds)
        {
            var max = GetStartFadeCap();
            if (startFadeSeconds < 0 || startFadeSeconds > max + 1e-9)
                return Fail<ExportPlan>("InvalidStartFade", "Start fade exceeds the first kept section.");
        }

        if (Settings.FadeToBlackSeconds is { } endFadeSeconds)
        {
            var max = GetEndFadeCap();
            if (endFadeSeconds < 0 || endFadeSeconds > max + 1e-9)
                return Fail<ExportPlan>("InvalidEndFade", "End fade exceeds the final kept section.");
        }

        return Ok(new ExportPlan(
            clips,
            joins.ToImmutable(),
            Settings.FadeFromBlackSeconds,
            Settings.FadeToBlackSeconds));
    }

    public double GetCrossfadeCap(TimeRange deleted)
    {
        var kept = GetKeptRuns();
        var beforeIndex = kept.FindLastIndex(k => k.End <= deleted.Start + MarkerEpsilon);
        var afterIndex = kept.FindIndex(k => k.Start >= deleted.End - MarkerEpsilon);
        if (beforeIndex < 0 || afterIndex < 0)
            return 0;
        return Math.Min(
            kept[beforeIndex].Duration.TotalSeconds,
            kept[afterIndex].Duration.TotalSeconds);
    }

    /// <summary>
    /// Max start-fade duration: length of the first kept section, minus any crossfade
    /// taken from the end of that section by the following join.
    /// </summary>
    public double GetStartFadeCap()
    {
        var kept = GetKeptRuns();
        if (kept.Count == 0)
            return 0;

        var first = kept[0];
        var available = first.Duration.TotalSeconds;

        ImmutableList<DeletedRun> interiorDeleted = [.. GetDeletedRuns().Where(r => r.IsInterior)];
        if (kept.Count >= 2 && interiorDeleted.Count >= 1)
        {
            var outgoing = interiorDeleted[0].Join;
            if (outgoing is { Mode: TransitionMode.Crossfade, CrossfadeSeconds: > 0 })
                available -= outgoing.CrossfadeSeconds;
        }

        return Math.Max(0, available);
    }

    /// <summary>
    /// Max end-fade duration: length of the last kept section, minus any crossfade
    /// taken from the start of that section by the preceding join.
    /// </summary>
    public double GetEndFadeCap()
    {
        var kept = GetKeptRuns();
        if (kept.Count == 0)
            return 0;

        var last = kept[^1];
        var available = last.Duration.TotalSeconds;

        ImmutableList<DeletedRun> interiorDeleted = [.. GetDeletedRuns().Where(r => r.IsInterior)];
        if (kept.Count >= 2 && interiorDeleted.Count >= kept.Count - 1)
        {
            var incoming = interiorDeleted[kept.Count - 2].Join;
            if (incoming is { Mode: TransitionMode.Crossfade, CrossfadeSeconds: > 0 })
                available -= incoming.CrossfadeSeconds;
        }

        return Math.Max(0, available);
    }

    private Result<Timeline> ApplyDeletionFlags(Guid boundaryIdA, Guid boundaryIdB, bool deleted)
    {
        if (!TryGetBoundaryTime(boundaryIdA, out var timeA) || !TryGetBoundaryTime(boundaryIdB, out var timeB))
            return Fail<Timeline>("BoundaryNotFound", "One or both boundaries were not found.");

        if (timeA >= timeB)
            return Fail<Timeline>("InvalidRange", "Range start must be before end.");

        var oldRuns = SnapshotInteriorRuns();
        var boundaries = GetBoundaries();
        var newSegments = Segments;
        for (var i = 0; i < Segments.Count; i++)
        {
            var segStart = boundaries[i];
            var segEnd = boundaries[i + 1];
            if (segStart >= timeA && segEnd <= timeB)
                newSegments = newSegments.SetItem(i, new SegmentState(deleted));
        }

        return Ok(Rebuild(Duration, Markers, newSegments, oldRuns, Settings));
    }

    private bool TryGetBoundaryTime(Guid id, out TimeSpan time)
    {
        if (id == StartBoundId)
        {
            time = TimeSpan.Zero;
            return true;
        }

        if (id == EndBoundId)
        {
            time = Duration;
            return true;
        }

        var marker = Markers.FirstOrDefault(m => m.Id == id);
        if (marker is null)
        {
            time = default;
            return false;
        }

        time = marker.Time;
        return true;
    }

    private bool TryGetBoundaryIdAt(TimeSpan time, out Guid id)
    {
        if (time <= TimeSpan.Zero)
        {
            id = StartBoundId;
            return true;
        }

        if (time >= Duration)
        {
            id = EndBoundId;
            return true;
        }

        var marker = Markers.FirstOrDefault(m => m.Time == time);
        if (marker is null)
        {
            id = default;
            return false;
        }

        id = marker.Id;
        return true;
    }

    private ImmutableList<InteriorRunSnapshot> SnapshotInteriorRuns()
    {
        ImmutableList<TimeRange> ranges =
        [
            .. CollectDeletedRanges().Where(r => r.IsInteriorOf(Duration))
        ];

        return
        [
            .. ranges.Select((range, i) => new InteriorRunSnapshot(
                range,
                i < Joins.Count
                    ? Joins[i]
                    : new JoinTransition(Guid.NewGuid(), TransitionMode.HardCut, 0)))
        ];
    }

    private ImmutableList<TimeRange> CollectDeletedRanges() =>
        CollectContiguousSegmentRanges(deleted: true);

    private ImmutableList<TimeRange> CollectContiguousSegmentRanges(bool deleted)
    {
        var boundaries = GetBoundaries();
        var ranges = ImmutableList.CreateBuilder<TimeRange>();
        var i = 0;
        while (i < Segments.Count)
        {
            if (Segments[i].IsDeleted != deleted)
            {
                i++;
                continue;
            }

            var startIndex = i;
            while (i < Segments.Count && Segments[i].IsDeleted == deleted)
                i++;

            ranges.Add(new TimeRange(boundaries[startIndex], boundaries[i]));
        }

        return ranges.ToImmutable();
    }

    private static Timeline Rebuild(
        TimeSpan duration,
        ImmutableList<Marker> markers,
        ImmutableList<SegmentState> segments,
        ImmutableList<InteriorRunSnapshot> oldInteriorRuns,
        ProjectSettings settings)
    {
        var probe = new Timeline(
            duration, markers, segments, [], settings);

        ImmutableList<TimeRange> newRanges =
        [
            .. probe.CollectDeletedRanges().Where(r => r.IsInteriorOf(duration))
        ];

        ImmutableList<JoinTransition> newJoins =
        [
            .. newRanges.Select(range =>
            {
                ImmutableList<InteriorRunSnapshot> overlappingOld =
                [
                    .. oldInteriorRuns.Where(old => old.Range.Overlaps(range))
                ];

                // Preserve only for 1:1 expand/shrink. Split (1 old → many new) and
                // coalesce (many old → 1 new) both reset to HardCut.
                var isOneToOne = overlappingOld.Count == 1
                    && newRanges.Count(n => overlappingOld[0].Range.Overlaps(n)) == 1;

                var join = isOneToOne
                    ? overlappingOld[0].Join
                    : new JoinTransition(Guid.NewGuid(), TransitionMode.HardCut, 0);

                if (join.Mode != TransitionMode.Crossfade)
                    return join;

                var cap = probe.GetCrossfadeCap(range);
                if (cap <= 0 || join.CrossfadeSeconds <= 0)
                    return join with { Mode = TransitionMode.HardCut, CrossfadeSeconds = 0 };
                if (join.CrossfadeSeconds > cap)
                    return join with { CrossfadeSeconds = cap };
                return join;
            })
        ];

        settings = ClampSettings(probe, settings);
        return new Timeline(duration, markers, segments, newJoins, settings);
    }

    private static ProjectSettings ClampSettings(Timeline probe, ProjectSettings settings)
    {
        double? startSeconds = settings.FadeFromBlackSeconds;
        double? endSeconds = settings.FadeToBlackSeconds;

        if (startSeconds is { } start)
        {
            var max = probe.GetStartFadeCap();
            if (max <= 0)
                startSeconds = null;
            else if (start > max)
                startSeconds = max;
        }

        if (endSeconds is { } end)
        {
            var max = probe.GetEndFadeCap();
            if (max <= 0)
                endSeconds = null;
            else if (end > max)
                endSeconds = max;
        }

        return new ProjectSettings(startSeconds, endSeconds);
    }

    private static TimeSpan AbsDiff(TimeSpan a, TimeSpan b) => a > b ? a - b : b - a;
}
