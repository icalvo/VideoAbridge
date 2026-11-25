using System.Collections.Immutable;

namespace VideoAbridge.Core.Editing;

public sealed class Timeline
{
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
            return new DomainError("InvalidDuration", "Duration must be positive.");

        return Empty(duration);
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
            return new DomainError("InvalidDuration", "Duration must be positive.");
        if (segmentDeleted.Count != markers.Count + 1)
            return new DomainError("InvalidSegmentCount", "Segment count must equal marker count + 1.");
        if (markers.Any(m => m.Time <= TimeSpan.Zero || m.Time >= duration))
            return new DomainError("MarkerOutOfRange", "Marker time must be strictly inside the timeline.");

        ImmutableList<Marker> orderedMarkers = [.. markers.OrderBy(m => m.Time)];
        ImmutableList<SegmentState> segments = [.. segmentDeleted.Select(d => new SegmentState(d))];
        return new Timeline(duration, orderedMarkers, segments, joins, settings);
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
        ToDeletedRuns(CollectDeletedRanges());

    public ImmutableList<TimeRange> GetKeptRuns() =>
        CollectContiguousSegmentRanges(deleted: false);

    /// <summary>
    /// Kept and deleted runs from one segment walk (avoids duplicate scans when both are needed).
    /// </summary>
    public (ImmutableList<TimeRange> Kept, ImmutableList<DeletedRun> Deleted) GetKeptAndDeletedRuns()
    {
        var (kept, deletedRanges) = CollectKeptAndDeletedRanges();
        return (kept, ToDeletedRuns(deletedRanges));
    }

    /// <summary>Total duration of all non-deleted sections (the abridged output length).</summary>
    public TimeSpan GetAbridgedDuration() =>
        GetKeptRuns().Sum(run => run.Duration);

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
        AddMarker(time, source, MarkerId.New());

    public Result<Timeline> AddMarker(TimeSpan time, MarkerSource source, MarkerId id)
    {
        if (time <= TimeSpan.Zero || time >= Duration)
            return new DomainError("MarkerOutOfRange", "Marker time must be strictly inside the timeline.");

        if (Markers.Any(m => AbsDiff(m.Time, time) <= MarkerEpsilon))
            return new DomainError("MarkerTooClose", "A marker already exists near that time.");

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
        return new Timeline(Duration, newMarkers, newSegments, Joins, Settings);
    }

    /// <summary>
    /// Sets the display label on an existing marker. Null is treated as empty; whitespace is trimmed.
    /// </summary>
    public Result<Timeline> SetMarkerLabel(MarkerId markerId, string? label)
    {
        var index = Markers.FindIndex(m => m.Id == markerId);
        if (index < 0)
            return new DomainError("MarkerNotFound", "Marker was not found.");

        var normalized = string.IsNullOrWhiteSpace(label) ? "" : label.Trim();
        var marker = Markers[index];
        if (marker.Label == normalized)
            return this;

        return new Timeline(
            Duration,
            Markers.SetItem(index, marker with { Label = normalized }),
            Segments,
            Joins,
            Settings);
    }

    /// <summary>
    /// Moves an existing marker to <paramref name="newTime"/> without changing marker order.
    /// Fails when the time is out of range, too close to a neighbor/edge, would reorder markers,
    /// or would make an existing crossfade / edge fade exceed its cap.
    /// </summary>
    public Result<Timeline> MoveMarker(MarkerId markerId, TimeSpan newTime)
    {
        var index = Markers.FindIndex(m => m.Id == markerId);
        if (index < 0)
            return new DomainError("MarkerNotFound", "Marker was not found.");

        var marker = Markers[index];
        // Exact match only — MarkerEpsilon must not swallow intentional 1-frame nudges
        // (often ~16–40ms, ≤ epsilon at common frame rates).
        if (marker.Time == newTime)
            return this;

        if (newTime <= TimeSpan.Zero || newTime >= Duration)
            return new DomainError("MarkerOutOfRange", "Marker time must be strictly inside the timeline.");

        var leftBound = index == 0 ? TimeSpan.Zero : Markers[index - 1].Time;
        var rightBound = index == Markers.Count - 1 ? Duration : Markers[index + 1].Time;
        if (newTime <= leftBound
            || newTime >= rightBound
            || AbsDiff(leftBound, newTime) <= MarkerEpsilon
            || AbsDiff(rightBound, newTime) <= MarkerEpsilon)
        {
            return new DomainError(
                "MarkerTooClose",
                "Moving would cross or collide with an adjacent marker or timeline edge.");
        }

        var candidate = new Timeline(
            Duration,
            Markers.SetItem(index, marker with { Time = newTime }),
            Segments,
            Joins,
            Settings);
        if (candidate.GetTransitionFitError() is { } error)
            return error;
        return candidate;
    }

    /// <summary>
    /// Null when edge fades and interior crossfades fit their caps; otherwise the blocking error.
    /// </summary>
    private DomainError? GetTransitionFitError()
    {
        var (kept, deleted) = GetKeptAndDeletedRuns();

        if (Settings.FadeFromBlackSeconds is { } startFade)
        {
            var max = ComputeStartFadeCap(kept, deleted);
            if (startFade > max + 1e-9)
            {
                return new DomainError(
                    "StartFadeOutOfRange",
                    $"Moving would leave the start fade longer than the available {max:0.###}s.");
            }
        }

        if (Settings.FadeToBlackSeconds is { } endFade)
        {
            var max = ComputeEndFadeCap(kept, deleted);
            if (endFade > max + 1e-9)
            {
                return new DomainError(
                    "EndFadeOutOfRange",
                    $"Moving would leave the end fade longer than the available {max:0.###}s.");
            }
        }

        foreach (var run in deleted)
        {
            if (!run.IsInterior
                || run.Join is not { Mode: TransitionMode.Crossfade, Crossfade: var crossfade }
                || crossfade <= TimeSpan.Zero)
            {
                continue;
            }

            var cap = ComputeCrossfadeCap(run.Range, kept);
            if (crossfade > cap)
            {
                return new DomainError(
                    "CrossfadeOutOfRange",
                    $"Moving would make a crossfade exceed the available {cap.TotalSeconds:0.###}s cap.");
            }
        }

        return null;
    }

    /// <summary>Whether <see cref="MoveMarker"/> would succeed for this id and time.</summary>
    public bool CanMoveMarker(MarkerId markerId, TimeSpan newTime) =>
        GetMoveMarkerBlockReason(markerId, newTime) is null;

    /// <summary>Human-readable reason <see cref="MoveMarker"/> would fail, or null if it would succeed.</summary>
    public string? GetMoveMarkerBlockReason(MarkerId markerId, TimeSpan newTime) =>
        MoveMarker(markerId, newTime).Match(
            static _ => (string?)null,
            static error => error.Message);

    /// <summary>
    /// Whether <see cref="RemoveMarker"/> would succeed for this id (not scene-detect, not a keep/delete boundary).
    /// </summary>
    public bool CanRemoveMarker(MarkerId markerId) => GetRemoveMarkerBlockReason(markerId) is null;

    /// <summary>Human-readable reason <see cref="RemoveMarker"/> would fail, or null if it would succeed.</summary>
    public string? GetRemoveMarkerBlockReason(MarkerId markerId)
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

    public Result<Timeline> RemoveMarker(MarkerId markerId)
    {
        var index = Markers.FindIndex(m => m.Id == markerId);
        if (index < 0)
            return new DomainError("MarkerNotFound", "Marker was not found.");

        if (Markers[index].Source == MarkerSource.SceneDetect)
        {
            return new DomainError(
                "SceneMarkerProtected",
                "Scene-detect markers cannot be deleted.");
        }

        if (Segments[index].IsDeleted != Segments[index + 1].IsDeleted)
        {
            return new DomainError(
                "MarkerIsBoundary",
                "Cannot remove a marker that separates keep and delete segments.");
        }

        var oldRuns = SnapshotInteriorRuns();
        var newMarkers = Markers.RemoveAt(index);
        var newSegments = Segments.RemoveAt(index + 1);
        return Rebuild(Duration, newMarkers, newSegments, oldRuns, Settings);
    }

    public Result<Timeline> DeleteBetween(MarkerId boundaryIdA, MarkerId boundaryIdB) =>
        ApplyDeletionFlags(boundaryIdA, boundaryIdB, deleted: true);

    public Result<Timeline> RestoreBetween(MarkerId boundaryIdA, MarkerId boundaryIdB) =>
        ApplyDeletionFlags(boundaryIdA, boundaryIdB, deleted: false);

    /// <summary>
    /// Resolves the marker (or start/end) boundaries that enclose <paramref name="time"/>.
    /// Fails when <paramref name="time"/> is outside the open timeline or exactly on a boundary.
    /// </summary>
    public bool TryGetSurroundingBoundaries(TimeSpan time, out MarkerId left, out MarkerId right)
    {
        left = default;
        right = default;
        if (time <= TimeSpan.Zero || time >= Duration)
            return false;

        left = MarkerId.StartBound;
        right = MarkerId.EndBound;
        foreach (var marker in Markers)
        {
            if (marker.Time == time)
            {
                left = default;
                right = default;
                return false;
            }

            if (marker.Time < time)
                left = marker.Id;
            else
            {
                right = marker.Id;
                break;
            }
        }

        return true;
    }

    /// <summary>
    /// True when the open range between the boundaries contains at least one segment whose
    /// deleted flag equals <paramref name="isDeleted"/>.
    /// </summary>
    public bool RangeContainsSegment(MarkerId boundaryIdA, MarkerId boundaryIdB, bool isDeleted)
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

    public Result<Timeline> RestoreDeletedRun(DeletedRunId deletedRunId)
    {
        var run = GetDeletedRuns().FirstOrDefault(r => r.Id == deletedRunId);
        if (run is null)
            return new DomainError("DeletedRunNotFound", "Deleted run was not found.");

        if (!TryGetDeletedRunBoundaries(deletedRunId, out var left, out var right))
            return new DomainError("BoundaryNotFound", "Could not resolve deleted run boundaries.");

        return RestoreBetween(left, right);
    }

    /// <summary>
    /// Boundary marker ids of a deleted run (may be <see cref="MarkerId.StartBound"/> /
    /// <see cref="MarkerId.EndBound"/> for edge runs).
    /// </summary>
    public bool TryGetDeletedRunBoundaries(
        DeletedRunId deletedRunId,
        out MarkerId leftId,
        out MarkerId rightId)
    {
        var run = GetDeletedRuns().FirstOrDefault(r => r.Id == deletedRunId);
        if (run is null
            || !TryGetBoundaryIdAt(run.Range.Start, out leftId)
            || !TryGetBoundaryIdAt(run.Range.End, out rightId))
        {
            leftId = default;
            rightId = default;
            return false;
        }

        return true;
    }

    public Result<Timeline> SetJoinHardCut(DeletedRunId deletedRunId)
    {
        var index = Joins.FindIndex(j => j.DeletedRunId == deletedRunId);
        if (index < 0)
            return new DomainError("JoinNotFound", "Deleted run join was not found.");

        var updated = Joins[index] with { Mode = TransitionMode.HardCut, Crossfade = TimeSpan.Zero };
        return new Timeline(
            Duration, Markers, Segments, Joins.SetItem(index, updated), Settings);
    }

    public Result<Timeline> SetJoinCrossfade(DeletedRunId deletedRunId, TimeSpan crossfade)
    {
        var index = Joins.FindIndex(j => j.DeletedRunId == deletedRunId);
        if (index < 0)
            return new DomainError("JoinNotFound", "Deleted run join was not found.");

        var (kept, deletedRuns) = GetKeptAndDeletedRuns();
        var run = deletedRuns.FirstOrDefault(r => r.Id == deletedRunId);
        if (run is null || !run.IsInterior)
            return new DomainError("JoinNotInterior", "Crossfade requires an interior deleted run.");

        var cap = ComputeCrossfadeCap(run.Range, kept);
        if (cap <= TimeSpan.Zero)
            return new DomainError("CrossfadeCapZero", "Adjacent kept sections are too short for a crossfade.");

        if (crossfade <= TimeSpan.Zero || crossfade > cap)
        {
            return new DomainError(
                "CrossfadeOutOfRange",
                $"Crossfade must be in (0, {cap.TotalSeconds:0.###}] seconds.");
        }

        var updated = Joins[index] with { Mode = TransitionMode.Crossfade, Crossfade = crossfade };
        return new Timeline(
            Duration, Markers, Segments, Joins.SetItem(index, updated), Settings);
    }

    /// <summary>Sets start fade; <paramref name="seconds"/> null disables fade-from-black.</summary>
    public Result<Timeline> SetStartFade(double? seconds)
    {
        if (seconds is { } value)
        {
            var max = GetStartFadeCap();
            if (value < 0 || value > max + 1e-9)
            {
                return new DomainError(
                    "StartFadeOutOfRange",
                    $"Start fade must be between 0 and {max:0.###} seconds.");
            }
        }

        var settings = Settings with { FadeFromBlackSeconds = seconds };
        return new Timeline(Duration, Markers, Segments, Joins, settings);
    }

    /// <summary>Sets end fade; <paramref name="seconds"/> null disables fade-to-black.</summary>
    public Result<Timeline> SetEndFade(double? seconds)
    {
        if (seconds is { } value)
        {
            var max = GetEndFadeCap();
            if (value < 0 || value > max + 1e-9)
            {
                return new DomainError(
                    "EndFadeOutOfRange",
                    $"End fade must be between 0 and {max:0.###} seconds.");
            }
        }

        var settings = Settings with { FadeToBlackSeconds = seconds };
        return new Timeline(Duration, Markers, Segments, Joins, settings);
    }

    public Result<ExportPlan> TryBuildExportPlan()
    {
        var (kept, deletedRuns) = GetKeptAndDeletedRuns();
        return TryBuildExportPlan(kept, deletedRuns);
    }

    public Result<ExportPlan> TryBuildExportPlan(
        ImmutableList<TimeRange> kept,
        ImmutableList<DeletedRun> deletedRuns)
    {
        ArgumentNullException.ThrowIfNull(kept);
        ArgumentNullException.ThrowIfNull(deletedRuns);
        if (kept.Count == 0)
            return new DomainError("NothingToExport", "There is no kept material to export.");

        ImmutableList<TimeRange> clips = [.. kept];
        ImmutableList<DeletedRun> deleted = [.. deletedRuns.Where(r => r.IsInterior)];

        if (clips.Count != deleted.Count + 1)
            return new DomainError("InconsistentRuns", "Kept/deleted run topology is inconsistent.");

        var joins = ImmutableList.CreateBuilder<ExportJoin>();
        for (var i = 0; i < deleted.Count; i++)
        {
            var run = deleted[i];
            var join = run.Join ?? new JoinTransition(run.Id, TransitionMode.HardCut, TimeSpan.Zero);
            if (join.Mode == TransitionMode.Crossfade)
            {
                var cap = TimeSpans.Min(clips[i].Duration, clips[i + 1].Duration);
                if (join.Crossfade <= TimeSpan.Zero || join.Crossfade > cap)
                    return new DomainError("InvalidCrossfade", "A crossfade exceeds the adjacent kept duration cap.");
            }

            joins.Add(new ExportJoin(
                join.Mode,
                join.Mode == TransitionMode.Crossfade ? join.Crossfade : TimeSpan.Zero));
        }

        if (Settings.FadeFromBlackSeconds is { } startFadeSeconds)
        {
            var max = ComputeStartFadeCap(kept, deletedRuns);
            if (startFadeSeconds < 0 || startFadeSeconds > max + 1e-9)
                return new DomainError("InvalidStartFade", "Start fade exceeds the first kept section.");
        }

        if (Settings.FadeToBlackSeconds is { } endFadeSeconds)
        {
            var max = ComputeEndFadeCap(kept, deletedRuns);
            if (endFadeSeconds < 0 || endFadeSeconds > max + 1e-9)
                return new DomainError("InvalidEndFade", "End fade exceeds the final kept section.");
        }

        return new ExportPlan(
            clips,
            joins.ToImmutable(),
            Settings.FadeFromBlackSeconds,
            Settings.FadeToBlackSeconds);
    }

    public TimeSpan GetCrossfadeCap(TimeRange deleted) =>
        ComputeCrossfadeCap(deleted, GetKeptRuns());

    public static TimeSpan GetCrossfadeCap(TimeRange deleted, ImmutableList<TimeRange> kept) =>
        ComputeCrossfadeCap(deleted, kept);

    private static TimeSpan ComputeCrossfadeCap(TimeRange deleted, ImmutableList<TimeRange> kept)
    {
        ArgumentNullException.ThrowIfNull(kept);
        var beforeIndex = kept.FindLastIndex(k => k.End <= deleted.Start + MarkerEpsilon);
        var afterIndex = kept.FindIndex(k => k.Start >= deleted.End - MarkerEpsilon);
        if (beforeIndex < 0 || afterIndex < 0)
            return TimeSpan.Zero;
        return TimeSpans.Min(kept[beforeIndex].Duration, kept[afterIndex].Duration);
    }

    /// <summary>
    /// Max start-fade duration: length of the first kept section, minus any crossfade
    /// taken from the end of that section by the following join.
    /// </summary>
    public double GetStartFadeCap()
    {
        var (kept, deleted) = GetKeptAndDeletedRuns();
        return ComputeStartFadeCap(kept, deleted);
    }

    public static double GetStartFadeCap(
        ImmutableList<TimeRange> kept,
        ImmutableList<DeletedRun> deletedRuns) =>
        ComputeStartFadeCap(kept, deletedRuns);

    private static double ComputeStartFadeCap(
        ImmutableList<TimeRange> kept,
        ImmutableList<DeletedRun> deletedRuns)
    {
        ArgumentNullException.ThrowIfNull(kept);
        ArgumentNullException.ThrowIfNull(deletedRuns);
        if (kept.Count == 0)
            return 0;

        var available = kept[0].Duration;
        ImmutableList<DeletedRun> interiorDeleted = [.. deletedRuns.Where(r => r.IsInterior)];
        if (kept.Count >= 2 && interiorDeleted.Count >= 1)
        {
            var outgoing = interiorDeleted[0].Join;
            if (outgoing is { Mode: TransitionMode.Crossfade } join && join.Crossfade > TimeSpan.Zero)
                available -= join.Crossfade;
        }

        return TimeSpans.Max(TimeSpan.Zero, available).TotalSeconds;
    }

    /// <summary>
    /// Max end-fade duration: length of the last kept section, minus any crossfade
    /// taken from the start of that section by the preceding join.
    /// </summary>
    public double GetEndFadeCap()
    {
        var (kept, deleted) = GetKeptAndDeletedRuns();
        return ComputeEndFadeCap(kept, deleted);
    }

    public static double GetEndFadeCap(
        ImmutableList<TimeRange> kept,
        ImmutableList<DeletedRun> deletedRuns) =>
        ComputeEndFadeCap(kept, deletedRuns);

    private static double ComputeEndFadeCap(
        ImmutableList<TimeRange> kept,
        ImmutableList<DeletedRun> deletedRuns)
    {
        ArgumentNullException.ThrowIfNull(kept);
        ArgumentNullException.ThrowIfNull(deletedRuns);
        if (kept.Count == 0)
            return 0;

        var available = kept[^1].Duration;
        ImmutableList<DeletedRun> interiorDeleted = [.. deletedRuns.Where(r => r.IsInterior)];
        if (kept.Count >= 2 && interiorDeleted.Count >= kept.Count - 1)
        {
            var incoming = interiorDeleted[kept.Count - 2].Join;
            if (incoming is { Mode: TransitionMode.Crossfade } join && join.Crossfade > TimeSpan.Zero)
                available -= join.Crossfade;
        }

        return TimeSpans.Max(TimeSpan.Zero, available).TotalSeconds;
    }

    /// <summary>
    /// Source-time ranges of kept material consumed by edge fades and crossfades
    /// (Edit-mode timeline shading).
    /// </summary>
    public ImmutableList<TimeRange> GetTransitionShadeRanges()
    {
        var (kept, deleted) = GetKeptAndDeletedRuns();
        return GetTransitionShadeRanges(kept, deleted);
    }

    public ImmutableList<TimeRange> GetTransitionShadeRanges(
        ImmutableList<TimeRange> kept,
        ImmutableList<DeletedRun> deletedRuns)
    {
        ArgumentNullException.ThrowIfNull(kept);
        ArgumentNullException.ThrowIfNull(deletedRuns);
        if (kept.Count == 0)
            return [];

        var ranges = ImmutableList.CreateBuilder<TimeRange>();

        if (Settings.FadeFromBlackSeconds is > 0 and var fadeIn)
        {
            var first = kept[0];
            var fade = TimeSpans.Min(TimeSpan.FromSeconds(fadeIn), first.Duration);
            if (fade > TimeSpan.Zero)
                ranges.Add(new TimeRange(first.Start, first.Start + fade));
        }

        if (Settings.FadeToBlackSeconds is > 0 and var fadeOut)
        {
            var last = kept[^1];
            var fade = TimeSpans.Min(TimeSpan.FromSeconds(fadeOut), last.Duration);
            if (fade > TimeSpan.Zero)
                ranges.Add(new TimeRange(last.End - fade, last.End));
        }

        foreach (var run in deletedRuns)
        {
            if (!run.IsInterior
                || run.Join is not { Mode: TransitionMode.Crossfade, Crossfade: var crossfade }
                || crossfade <= TimeSpan.Zero)
            {
                continue;
            }

            var beforeIndex = kept.FindLastIndex(k => k.End <= run.Range.Start + MarkerEpsilon);
            var afterIndex = kept.FindIndex(k => k.Start >= run.Range.End - MarkerEpsilon);
            if (beforeIndex < 0 || afterIndex < 0)
                continue;

            var before = kept[beforeIndex];
            var beforeFade = TimeSpans.Min(crossfade, before.Duration);
            if (beforeFade > TimeSpan.Zero)
                ranges.Add(new TimeRange(before.End - beforeFade, before.End));

            var after = kept[afterIndex];
            var afterFade = TimeSpans.Min(crossfade, after.Duration);
            if (afterFade > TimeSpan.Zero)
                ranges.Add(new TimeRange(after.Start, after.Start + afterFade));
        }

        return ranges.ToImmutable();
    }

    private Result<Timeline> ApplyDeletionFlags(MarkerId boundaryIdA, MarkerId boundaryIdB, bool deleted)
    {
        if (!TryGetBoundaryTime(boundaryIdA, out var timeA) || !TryGetBoundaryTime(boundaryIdB, out var timeB))
            return new DomainError("BoundaryNotFound", "One or both boundaries were not found.");

        if (timeA >= timeB)
            return new DomainError("InvalidRange", "Range start must be before end.");

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

        return Rebuild(Duration, Markers, newSegments, oldRuns, Settings);
    }

    private bool TryGetBoundaryTime(MarkerId id, out TimeSpan time)
    {
        if (id == MarkerId.StartBound)
        {
            time = TimeSpan.Zero;
            return true;
        }

        if (id == MarkerId.EndBound)
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

    private bool TryGetBoundaryIdAt(TimeSpan time, out MarkerId id)
    {
        if (time <= TimeSpan.Zero)
        {
            id = MarkerId.StartBound;
            return true;
        }

        if (time >= Duration)
        {
            id = MarkerId.EndBound;
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
                    : new JoinTransition(DeletedRunId.New(), TransitionMode.HardCut, TimeSpan.Zero)))
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

    private (ImmutableList<TimeRange> Kept, ImmutableList<TimeRange> Deleted) CollectKeptAndDeletedRanges()
    {
        var boundaries = GetBoundaries();
        var kept = ImmutableList.CreateBuilder<TimeRange>();
        var deleted = ImmutableList.CreateBuilder<TimeRange>();
        var i = 0;
        while (i < Segments.Count)
        {
            var isDeleted = Segments[i].IsDeleted;
            var startIndex = i;
            while (i < Segments.Count && Segments[i].IsDeleted == isDeleted)
                i++;

            var range = new TimeRange(boundaries[startIndex], boundaries[i]);
            if (isDeleted)
                deleted.Add(range);
            else
                kept.Add(range);
        }

        return (kept.ToImmutable(), deleted.ToImmutable());
    }

    private ImmutableList<DeletedRun> ToDeletedRuns(ImmutableList<TimeRange> deletedRanges) =>
        deletedRanges.Aggregate(
            (Runs: ImmutableList<DeletedRun>.Empty, InteriorIndex: 0),
            (state, range) =>
            {
                if (!range.IsInteriorOf(Duration))
                {
                    var edgeId = range.Covers(Duration)
                        ? DeletedRunId.Full
                        : range.TouchesStart
                            ? DeletedRunId.Leading
                            : DeletedRunId.Trailing;
                    return (state.Runs.Add(new DeletedRun(edgeId, range, false, null)), state.InteriorIndex);
                }

                var join = state.InteriorIndex < Joins.Count
                    ? Joins[state.InteriorIndex]
                    : new JoinTransition(DeletedRunId.New(), TransitionMode.HardCut, TimeSpan.Zero);
                return (
                    state.Runs.Add(new DeletedRun(join.DeletedRunId, range, true, join)),
                    state.InteriorIndex + 1);
            }).Runs;

    private static Timeline Rebuild(
        TimeSpan duration,
        ImmutableList<Marker> markers,
        ImmutableList<SegmentState> segments,
        ImmutableList<InteriorRunSnapshot> oldInteriorRuns,
        ProjectSettings settings)
    {
        var probe = new Timeline(
            duration, markers, segments, [], settings);

        var (kept, deletedRanges) = probe.CollectKeptAndDeletedRanges();
        ImmutableList<TimeRange> newRanges =
        [
            .. deletedRanges.Where(r => r.IsInteriorOf(duration))
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
                    : new JoinTransition(DeletedRunId.New(), TransitionMode.HardCut, TimeSpan.Zero);

                if (join.Mode != TransitionMode.Crossfade)
                    return join;

                var cap = ComputeCrossfadeCap(range, kept);
                if (cap <= TimeSpan.Zero || join.Crossfade <= TimeSpan.Zero)
                    return join with { Mode = TransitionMode.HardCut, Crossfade = TimeSpan.Zero };
                if (join.Crossfade > cap)
                    return join with { Crossfade = cap };
                return join;
            })
        ];

        var withJoins = new Timeline(duration, markers, segments, newJoins, settings);
        settings = ClampSettings(kept, withJoins.ToDeletedRuns(deletedRanges), settings);
        return new Timeline(duration, markers, segments, newJoins, settings);
    }

    private static ProjectSettings ClampSettings(
        ImmutableList<TimeRange> kept,
        ImmutableList<DeletedRun> deletedRuns,
        ProjectSettings settings)
    {
        double? startSeconds = settings.FadeFromBlackSeconds;
        double? endSeconds = settings.FadeToBlackSeconds;

        if (startSeconds is { } start)
        {
            var max = ComputeStartFadeCap(kept, deletedRuns);
            if (max <= 0)
                startSeconds = null;
            else if (start > max)
                startSeconds = max;
        }

        if (endSeconds is { } end)
        {
            var max = ComputeEndFadeCap(kept, deletedRuns);
            if (max <= 0)
                endSeconds = null;
            else if (end > max)
                endSeconds = max;
        }

        return new ProjectSettings(startSeconds, endSeconds);
    }

    private static TimeSpan AbsDiff(TimeSpan a, TimeSpan b) => a > b ? a - b : b - a;
}
