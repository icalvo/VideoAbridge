namespace VideoAbridge.Core.Editing;

/// <summary>
/// Edit-mode timeline keyboard focus: two lanes (markers vs deleted runs),
/// horizontal prev/next with clamp, vertical nearest-in-time (tie → earlier).
/// </summary>
public static class TimelineFocusNavigation
{
    public static TimelineItemId? MoveHorizontal(
        TimelineItemId? current,
        int direction,
        IReadOnlyList<(TimelineItemId Id, TimeSpan Time)> markers,
        IReadOnlyList<(TimelineItemId Id, TimeSpan Time)> deletedRuns)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(deletedRuns);
        if (direction == 0)
            return current;

        if (current is not { } focus)
            return PickDefault(markers, deletedRuns);

        var lane = focus switch
        {
            MarkerId => markers,
            DeletedRunId => deletedRuns,
        };

        if (lane.Count == 0)
            return focus;

        var index = IndexOf(lane, focus);
        if (index < 0)
            return NearestInLane(lane, TimeSpan.Zero);

        var next = Math.Clamp(index + Math.Sign(direction), 0, lane.Count - 1);
        return lane[next].Id;
    }

    public static TimelineItemId? MoveVertical(
        TimelineItemId? current,
        IReadOnlyList<(TimelineItemId Id, TimeSpan Time)> markers,
        IReadOnlyList<(TimelineItemId Id, TimeSpan Time)> deletedRuns)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(deletedRuns);

        if (current is not { } focus)
            return PickDefault(markers, deletedRuns);

        return focus switch
        {
            MarkerId => deletedRuns.Count == 0
                ? focus
                : NearestInLane(deletedRuns, TimeOf(markers, focus) ?? TimeSpan.Zero) ?? focus,
            DeletedRunId => markers.Count == 0
                ? focus
                : NearestInLane(markers, TimeOf(deletedRuns, focus) ?? TimeSpan.Zero) ?? focus,
        };
    }

    /// <summary>Nearest marker to <paramref name="time"/>, else nearest deleted run, else null.</summary>
    public static TimelineItemId? NearestToTime(
        TimeSpan time,
        IReadOnlyList<(TimelineItemId Id, TimeSpan Time)> markers,
        IReadOnlyList<(TimelineItemId Id, TimeSpan Time)> deletedRuns)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(deletedRuns);

        return NearestInLane(markers, time) ?? NearestInLane(deletedRuns, time);
    }

    /// <summary>
    /// After the focused item disappears: nearest remaining in the same lane,
    /// else the other lane, else null.
    /// </summary>
    public static TimelineItemId? AfterRemoval(
        TimelineItemId removed,
        TimeSpan removedTime,
        IReadOnlyList<(TimelineItemId Id, TimeSpan Time)> markers,
        IReadOnlyList<(TimelineItemId Id, TimeSpan Time)> deletedRuns)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(deletedRuns);

        var (sameLane, otherLane) = removed switch
        {
            MarkerId => (markers, deletedRuns),
            DeletedRunId => (deletedRuns, markers),
        };

        return NearestInLane(sameLane, removedTime) ?? NearestInLane(otherLane, removedTime);
    }

    public static TimelineItemId? PickDefault(
        IReadOnlyList<(TimelineItemId Id, TimeSpan Time)> markers,
        IReadOnlyList<(TimelineItemId Id, TimeSpan Time)> deletedRuns)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(deletedRuns);

        if (markers.Count > 0)
            return markers[0].Id;
        if (deletedRuns.Count > 0)
            return deletedRuns[0].Id;
        return null;
    }

    /// <summary>Nearest id by absolute time distance; ties prefer the earlier item.</summary>
    public static TimelineItemId? NearestInLane(
        IReadOnlyList<(TimelineItemId Id, TimeSpan Time)> lane,
        TimeSpan time)
    {
        ArgumentNullException.ThrowIfNull(lane);
        if (lane.Count == 0)
            return null;

        TimelineItemId? bestId = null;
        var bestDist = TimeSpan.MaxValue;
        var bestTime = TimeSpan.MaxValue;

        foreach (var (id, itemTime) in lane)
        {
            var dist = AbsDiff(itemTime, time);
            if (dist < bestDist || (dist == bestDist && itemTime < bestTime))
            {
                bestDist = dist;
                bestTime = itemTime;
                bestId = id;
            }
        }

        return bestId;
    }

    private static int IndexOf(IReadOnlyList<(TimelineItemId Id, TimeSpan Time)> lane, TimelineItemId id)
    {
        for (var i = 0; i < lane.Count; i++)
        {
            if (lane[i].Id == id)
                return i;
        }

        return -1;
    }

    private static TimeSpan? TimeOf(IReadOnlyList<(TimelineItemId Id, TimeSpan Time)> lane, TimelineItemId id)
    {
        foreach (var (itemId, time) in lane)
        {
            if (itemId == id)
                return time;
        }

        return null;
    }

    private static TimeSpan AbsDiff(TimeSpan a, TimeSpan b) => a > b ? a - b : b - a;
}
