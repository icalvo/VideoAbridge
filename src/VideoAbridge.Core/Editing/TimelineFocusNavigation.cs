namespace VideoAbridge.Core.Editing;

/// <summary>
/// Edit-mode playhead-driven timeline focus: derive the S/Shift+S target from the
/// playhead, and compute prev/next marker or deleted-run center jump targets.
/// </summary>
public static class TimelineFocusNavigation
{
    /// <summary>
    /// Exact marker at <paramref name="playhead"/>, else deleted run containing it, else null.
    /// Marker wins when both apply (boundary markers).
    /// </summary>
    public static TimelineItemId? ResolveAtPlayhead(
        TimeSpan playhead,
        IReadOnlyList<(TimelineItemId Id, TimeSpan Time)> markers,
        IReadOnlyList<(TimelineItemId Id, TimeSpan Start, TimeSpan End)> deletedRuns)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(deletedRuns);

        foreach (var (id, time) in markers)
        {
            if (time == playhead)
                return id;
        }

        foreach (var (id, start, end) in deletedRuns)
        {
            if (playhead >= start && playhead <= end)
                return id;
        }

        return null;
    }

    /// <summary>
    /// Previous marker strictly before <paramref name="playhead"/>; if already on a marker,
    /// the neighbor before it. Clamps to the earliest marker (no wrap). Null if none.
    /// </summary>
    public static TimeSpan? PreviousMarkerTime(
        TimeSpan playhead,
        IReadOnlyList<(TimelineItemId Id, TimeSpan Time)> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        TimeSpan? best = null;
        foreach (var (_, time) in markers)
        {
            if (time >= playhead)
                continue;
            if (best is null || time > best.Value)
                best = time;
        }

        return best;
    }

    /// <summary>
    /// Next marker strictly after <paramref name="playhead"/>; if already on a marker,
    /// the neighbor after it. Clamps to the latest marker (no wrap). Null if none.
    /// </summary>
    public static TimeSpan? NextMarkerTime(
        TimeSpan playhead,
        IReadOnlyList<(TimelineItemId Id, TimeSpan Time)> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        TimeSpan? best = null;
        foreach (var (_, time) in markers)
        {
            if (time <= playhead)
                continue;
            if (best is null || time < best.Value)
                best = time;
        }

        return best;
    }

    /// <summary>
    /// Previous deleted-run center strictly before <paramref name="playhead"/>.
    /// Clamps to the earliest center (no wrap). Null if none.
    /// </summary>
    public static TimeSpan? PreviousCutCenter(
        TimeSpan playhead,
        IReadOnlyList<(TimelineItemId Id, TimeSpan Start, TimeSpan End)> deletedRuns)
    {
        ArgumentNullException.ThrowIfNull(deletedRuns);
        TimeSpan? best = null;
        foreach (var (_, start, end) in deletedRuns)
        {
            var center = Midpoint(start, end);
            if (center >= playhead)
                continue;
            if (best is null || center > best.Value)
                best = center;
        }

        return best;
    }

    /// <summary>
    /// Next deleted-run center strictly after <paramref name="playhead"/>.
    /// Clamps to the latest center (no wrap). Null if none.
    /// </summary>
    public static TimeSpan? NextCutCenter(
        TimeSpan playhead,
        IReadOnlyList<(TimelineItemId Id, TimeSpan Start, TimeSpan End)> deletedRuns)
    {
        ArgumentNullException.ThrowIfNull(deletedRuns);
        TimeSpan? best = null;
        foreach (var (_, start, end) in deletedRuns)
        {
            var center = Midpoint(start, end);
            if (center <= playhead)
                continue;
            if (best is null || center < best.Value)
                best = center;
        }

        return best;
    }

    private static TimeSpan Midpoint(TimeSpan start, TimeSpan end) =>
        TimeSpan.FromTicks((start.Ticks + end.Ticks) / 2);
}
