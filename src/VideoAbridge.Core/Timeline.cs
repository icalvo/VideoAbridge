using System.Collections.Immutable;

namespace VideoAbridge.Core;

public sealed class Timeline
{
    public static readonly Guid StartBoundId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid EndBoundId = Guid.Parse("00000000-0000-0000-0000-000000000002");
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

    public static Timeline Create(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");

        return new Timeline(
            duration,
            ImmutableList<Marker>.Empty,
            ImmutableList.Create(new SegmentState(IsDeleted: false)),
            ImmutableList<JoinTransition>.Empty,
            ProjectSettings.Default);
    }

    /// <summary>
    /// Builds a timeline from persisted state without applying stepwise topology rules.
    /// Factory may throw only for programmer errors (count mismatches); callers should validate first.
    /// </summary>
    public static Timeline FromPersistedState(
        TimeSpan duration,
        IReadOnlyList<Marker> markers,
        IReadOnlyList<bool> segmentDeleted,
        IReadOnlyList<JoinTransition> joins,
        ProjectSettings settings)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        if (segmentDeleted.Count != markers.Count + 1)
            throw new ArgumentException("Segment count must equal marker count + 1.");

        var orderedMarkers = markers.OrderBy(m => m.Time).ToImmutableList();
        var segments = segmentDeleted.Select(d => new SegmentState(d)).ToImmutableList();
        var joinList = joins.ToImmutableList();
        return new Timeline(duration, orderedMarkers, segments, joinList, settings);
    }

    public IReadOnlyList<TimeSpan> GetBoundaries()
    {
        var list = new List<TimeSpan>(Markers.Count + 2) { TimeSpan.Zero };
        foreach (var marker in Markers)
            list.Add(marker.Time);
        list.Add(Duration);
        return list;
    }

    public IReadOnlyList<DeletedRun> GetDeletedRuns()
    {
        var ranges = CollectDeletedRanges();
        var result = new List<DeletedRun>(ranges.Count);
        var interiorIndex = 0;

        foreach (var (start, end) in ranges)
        {
            var isInterior = start > TimeSpan.Zero && end < Duration;
            if (!isInterior)
            {
                result.Add(new DeletedRun(Guid.NewGuid(), start, end, false, null));
                continue;
            }

            var join = interiorIndex < Joins.Count
                ? Joins[interiorIndex]
                : new JoinTransition(Guid.NewGuid(), TransitionMode.HardCut, 0);
            interiorIndex++;
            result.Add(new DeletedRun(join.DeletedRunId, start, end, true, join));
        }

        return result;
    }

    public IReadOnlyList<KeptRun> GetKeptRuns()
    {
        var boundaries = GetBoundaries();
        var runs = new List<KeptRun>();
        var i = 0;
        while (i < Segments.Count)
        {
            if (Segments[i].IsDeleted)
            {
                i++;
                continue;
            }

            var startIndex = i;
            while (i < Segments.Count && !Segments[i].IsDeleted)
                i++;

            runs.Add(new KeptRun(boundaries[startIndex], boundaries[i]));
        }

        return runs;
    }

    public Result<Timeline> AddMarker(TimeSpan time, MarkerSource source) =>
        AddMarker(time, source, Guid.NewGuid());

    public Result<Timeline> AddMarker(TimeSpan time, MarkerSource source, Guid id)
    {
        if (time <= TimeSpan.Zero || time >= Duration)
            return Result<Timeline>.Fail("MarkerOutOfRange", "Marker time must be strictly inside the timeline.");

        if (Markers.Any(m => AbsDiff(m.Time, time) <= MarkerEpsilon))
            return Result<Timeline>.Fail("MarkerTooClose", "A marker already exists near that time.");

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
        return Result<Timeline>.Ok(new Timeline(Duration, newMarkers, newSegments, Joins, Settings));
    }

    public Result<Timeline> AddMarkers(IEnumerable<(TimeSpan Time, MarkerSource Source)> markers)
    {
        var current = this;
        foreach (var (time, source) in markers.OrderBy(m => m.Time))
        {
            if (time <= TimeSpan.Zero || time >= current.Duration)
                continue;
            if (current.Markers.Any(m => AbsDiff(m.Time, time) <= MarkerEpsilon))
                continue;

            var result = current.AddMarker(time, source);
            if (!result.IsSuccess)
                return result;
            current = result.Value!;
        }

        return Result<Timeline>.Ok(current);
    }

    public Result<Timeline> RemoveMarker(Guid markerId)
    {
        var index = Markers.FindIndex(m => m.Id == markerId);
        if (index < 0)
            return Result<Timeline>.Fail("MarkerNotFound", "Marker was not found.");

        if (Segments[index].IsDeleted != Segments[index + 1].IsDeleted)
        {
            return Result<Timeline>.Fail(
                "MarkerIsBoundary",
                "Cannot remove a marker that separates keep and delete segments.");
        }

        var oldRuns = SnapshotInteriorRuns();
        var newMarkers = Markers.RemoveAt(index);
        var newSegments = Segments.RemoveAt(index + 1);
        return Result<Timeline>.Ok(Rebuild(Duration, newMarkers, newSegments, oldRuns, Settings));
    }

    public Result<Timeline> MoveMarker(Guid markerId, TimeSpan newTime)
    {
        var index = Markers.FindIndex(m => m.Id == markerId);
        if (index < 0)
            return Result<Timeline>.Fail("MarkerNotFound", "Marker was not found.");

        var previous = index == 0 ? TimeSpan.Zero : Markers[index - 1].Time;
        var next = index == Markers.Count - 1 ? Duration : Markers[index + 1].Time;

        if (newTime <= previous + MarkerEpsilon || newTime >= next - MarkerEpsilon)
        {
            return Result<Timeline>.Fail(
                "MarkerMoveOutOfRange",
                "Marker must stay strictly between its neighbors.");
        }

        var oldRuns = SnapshotInteriorRuns();
        var old = Markers[index];
        var newMarkers = Markers.SetItem(index, old with { Time = newTime });
        return Result<Timeline>.Ok(Rebuild(Duration, newMarkers, Segments, oldRuns, Settings));
    }

    public Result<Timeline> DeleteBetween(Guid boundaryIdA, Guid boundaryIdB) =>
        ApplyDeletionFlags(boundaryIdA, boundaryIdB, deleted: true);

    public Result<Timeline> RestoreBetween(Guid boundaryIdA, Guid boundaryIdB) =>
        ApplyDeletionFlags(boundaryIdA, boundaryIdB, deleted: false);

    public Result<Timeline> SetJoinHardCut(Guid deletedRunId)
    {
        var index = Joins.FindIndex(j => j.DeletedRunId == deletedRunId);
        if (index < 0)
            return Result<Timeline>.Fail("JoinNotFound", "Deleted run join was not found.");

        var updated = Joins[index] with { Mode = TransitionMode.HardCut, CrossfadeSeconds = 0 };
        return Result<Timeline>.Ok(new Timeline(
            Duration, Markers, Segments, Joins.SetItem(index, updated), Settings));
    }

    public Result<Timeline> SetJoinCrossfade(Guid deletedRunId, double seconds)
    {
        var index = Joins.FindIndex(j => j.DeletedRunId == deletedRunId);
        if (index < 0)
            return Result<Timeline>.Fail("JoinNotFound", "Deleted run join was not found.");

        var run = GetDeletedRuns().FirstOrDefault(r => r.Id == deletedRunId);
        if (run is null || !run.IsInterior)
            return Result<Timeline>.Fail("JoinNotInterior", "Crossfade requires an interior deleted run.");

        var cap = GetCrossfadeCap(run.Start, run.End);
        if (cap <= 0)
            return Result<Timeline>.Fail("CrossfadeCapZero", "Adjacent kept sections are too short for a crossfade.");

        if (seconds <= 0 || seconds > cap + 1e-9)
        {
            return Result<Timeline>.Fail(
                "CrossfadeOutOfRange",
                $"Crossfade must be in (0, {cap:0.###}] seconds.");
        }

        var updated = Joins[index] with { Mode = TransitionMode.Crossfade, CrossfadeSeconds = seconds };
        return Result<Timeline>.Ok(new Timeline(
            Duration, Markers, Segments, Joins.SetItem(index, updated), Settings));
    }

    public Result<Timeline> SetEndFade(bool enabled, double seconds)
    {
        if (enabled)
        {
            var finalKeep = GetKeptRuns().LastOrDefault();
            var max = finalKeep?.Duration.TotalSeconds ?? 0;
            if (seconds < 0 || seconds > max + 1e-9)
            {
                return Result<Timeline>.Fail(
                    "EndFadeOutOfRange",
                    $"End fade must be between 0 and {max:0.###} seconds.");
            }
        }
        else if (seconds < 0)
        {
            return Result<Timeline>.Fail("EndFadeOutOfRange", "End fade seconds cannot be negative.");
        }

        var settings = new ProjectSettings(enabled, enabled ? seconds : 0);
        return Result<Timeline>.Ok(new Timeline(Duration, Markers, Segments, Joins, settings));
    }

    public Result<ExportPlan> TryBuildExportPlan()
    {
        var kept = GetKeptRuns();
        if (kept.Count == 0)
            return Result<ExportPlan>.Fail("NothingToExport", "There is no kept material to export.");

        var clips = kept.Select(k => new KeptClip(k.Start, k.End)).ToList();
        var deleted = GetDeletedRuns().Where(r => r.IsInterior).ToList();

        if (clips.Count != deleted.Count + 1)
            return Result<ExportPlan>.Fail("InconsistentRuns", "Kept/deleted run topology is inconsistent.");

        var joins = new List<ExportJoin>(deleted.Count);
        for (var i = 0; i < deleted.Count; i++)
        {
            var run = deleted[i];
            var join = run.Join ?? new JoinTransition(run.Id, TransitionMode.HardCut, 0);
            if (join.Mode == TransitionMode.Crossfade)
            {
                var cap = Math.Min(clips[i].Duration.TotalSeconds, clips[i + 1].Duration.TotalSeconds);
                if (join.CrossfadeSeconds <= 0 || join.CrossfadeSeconds > cap + 1e-9)
                    return Result<ExportPlan>.Fail("InvalidCrossfade", "A crossfade exceeds the adjacent kept duration cap.");
            }

            joins.Add(new ExportJoin(
                join.Mode,
                join.Mode == TransitionMode.Crossfade ? join.CrossfadeSeconds : 0));
        }

        var fadeSeconds = Settings.FadeToBlackAtEnd ? Settings.FadeToBlackSeconds : 0;
        if (Settings.FadeToBlackAtEnd)
        {
            var finalDuration = clips[^1].Duration.TotalSeconds;
            if (fadeSeconds < 0 || fadeSeconds > finalDuration + 1e-9)
                return Result<ExportPlan>.Fail("InvalidEndFade", "End fade exceeds the final kept section.");
        }

        return Result<ExportPlan>.Ok(new ExportPlan(clips, joins, Settings.FadeToBlackAtEnd, fadeSeconds));
    }

    public double GetCrossfadeCap(TimeSpan deletedStart, TimeSpan deletedEnd)
    {
        var kept = GetKeptRuns();
        var before = kept.LastOrDefault(k => k.End <= deletedStart + MarkerEpsilon);
        var after = kept.FirstOrDefault(k => k.Start >= deletedEnd - MarkerEpsilon);
        if (before is null || after is null)
            return 0;
        return Math.Min(before.Duration.TotalSeconds, after.Duration.TotalSeconds);
    }

    private Result<Timeline> ApplyDeletionFlags(Guid boundaryIdA, Guid boundaryIdB, bool deleted)
    {
        if (!TryGetBoundaryTime(boundaryIdA, out var timeA) || !TryGetBoundaryTime(boundaryIdB, out var timeB))
            return Result<Timeline>.Fail("BoundaryNotFound", "One or both boundaries were not found.");

        if (timeA >= timeB)
            return Result<Timeline>.Fail("InvalidRange", "Range start must be before end.");

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

        return Result<Timeline>.Ok(Rebuild(Duration, Markers, newSegments, oldRuns, Settings));
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

    private List<(TimeSpan Start, TimeSpan End, JoinTransition Join)> SnapshotInteriorRuns()
    {
        var ranges = CollectDeletedRanges()
            .Where(r => r.Start > TimeSpan.Zero && r.End < Duration)
            .ToList();

        var list = new List<(TimeSpan, TimeSpan, JoinTransition)>(ranges.Count);
        for (var i = 0; i < ranges.Count; i++)
        {
            var join = i < Joins.Count
                ? Joins[i]
                : new JoinTransition(Guid.NewGuid(), TransitionMode.HardCut, 0);
            list.Add((ranges[i].Start, ranges[i].End, join));
        }

        return list;
    }

    private List<(TimeSpan Start, TimeSpan End)> CollectDeletedRanges()
    {
        var boundaries = GetBoundaries();
        var ranges = new List<(TimeSpan, TimeSpan)>();
        var i = 0;
        while (i < Segments.Count)
        {
            if (!Segments[i].IsDeleted)
            {
                i++;
                continue;
            }

            var startIndex = i;
            while (i < Segments.Count && Segments[i].IsDeleted)
                i++;

            ranges.Add((boundaries[startIndex], boundaries[i]));
        }

        return ranges;
    }

    private static Timeline Rebuild(
        TimeSpan duration,
        ImmutableList<Marker> markers,
        ImmutableList<SegmentState> segments,
        List<(TimeSpan Start, TimeSpan End, JoinTransition Join)> oldInteriorRuns,
        ProjectSettings settings)
    {
        var probe = new Timeline(
            duration, markers, segments, ImmutableList<JoinTransition>.Empty, settings);

        var newRanges = probe.CollectDeletedRanges()
            .Where(r => r.Start > TimeSpan.Zero && r.End < duration)
            .ToList();

        var newJoins = ImmutableList.CreateBuilder<JoinTransition>();
        foreach (var (start, end) in newRanges)
        {
            var overlappingOld = oldInteriorRuns
                .Where(old => RangesOverlap(old.Start, old.End, start, end))
                .ToList();

            // Preserve only for 1:1 expand/shrink. Split (1 old → many new) and
            // coalesce (many old → 1 new) both reset to HardCut.
            var isOneToOne = overlappingOld.Count == 1
                && newRanges.Count(n => RangesOverlap(overlappingOld[0].Start, overlappingOld[0].End, n.Start, n.End)) == 1;

            var join = isOneToOne
                ? overlappingOld[0].Join
                : new JoinTransition(Guid.NewGuid(), TransitionMode.HardCut, 0);

            if (join.Mode == TransitionMode.Crossfade)
            {
                var cap = probe.GetCrossfadeCap(start, end);
                if (cap <= 0 || join.CrossfadeSeconds <= 0)
                    join = join with { Mode = TransitionMode.HardCut, CrossfadeSeconds = 0 };
                else if (join.CrossfadeSeconds > cap)
                    join = join with { CrossfadeSeconds = cap };
            }

            newJoins.Add(join);
        }

        settings = ClampSettings(probe, settings);
        return new Timeline(duration, markers, segments, newJoins.ToImmutable(), settings);
    }

    private static ProjectSettings ClampSettings(Timeline probe, ProjectSettings settings)
    {
        if (!settings.FadeToBlackAtEnd)
            return settings;

        var finalKeep = probe.GetKeptRuns().LastOrDefault();
        var max = finalKeep?.Duration.TotalSeconds ?? 0;
        if (max <= 0)
            return new ProjectSettings(false, 0);
        if (settings.FadeToBlackSeconds > max)
            return settings with { FadeToBlackSeconds = max };
        return settings;
    }

    private static bool RangesOverlap(TimeSpan aStart, TimeSpan aEnd, TimeSpan bStart, TimeSpan bEnd) =>
        aStart < bEnd && bStart < aEnd;

    private static TimeSpan AbsDiff(TimeSpan a, TimeSpan b) => a > b ? a - b : b - a;
}
