namespace VideoAbridge.Core.Editing;

/// <summary>
/// Edit-mode timeline keyboard focus: two lanes (markers vs deleted runs),
/// horizontal prev/next with clamp, vertical nearest-in-time (tie → earlier).
/// </summary>
public static class TimelineFocusNavigation
{
    public static TimelineFocusTarget MoveHorizontal(
        TimelineFocusTarget current,
        int direction,
        IReadOnlyList<(Guid Id, TimeSpan Time)> markers,
        IReadOnlyList<(Guid Id, TimeSpan Time)> deletedRuns)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(deletedRuns);
        if (direction == 0)
            return current;

        var lane = current.Kind switch
        {
            TimelineFocusKind.Marker => markers,
            TimelineFocusKind.DeletedRun => deletedRuns,
            _ => null,
        };

        if (lane is null || lane.Count == 0)
            return current.IsNone ? PickDefault(markers, deletedRuns) : current;

        var index = IndexOf(lane, current.Id);
        if (index < 0)
            return NearestInLane(lane, TimeSpan.Zero) is { } fallback
                ? new TimelineFocusTarget(current.Kind, fallback)
                : TimelineFocusTarget.None;

        var next = Math.Clamp(index + Math.Sign(direction), 0, lane.Count - 1);
        return new TimelineFocusTarget(current.Kind, lane[next].Id);
    }

    public static TimelineFocusTarget MoveVertical(
        TimelineFocusTarget current,
        IReadOnlyList<(Guid Id, TimeSpan Time)> markers,
        IReadOnlyList<(Guid Id, TimeSpan Time)> deletedRuns)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(deletedRuns);

        if (current.Kind == TimelineFocusKind.Marker)
        {
            if (deletedRuns.Count == 0)
                return current;
            var time = TimeOf(markers, current.Id) ?? TimeSpan.Zero;
            var id = NearestInLane(deletedRuns, time);
            return id is null
                ? current
                : new TimelineFocusTarget(TimelineFocusKind.DeletedRun, id.Value);
        }

        if (current.Kind == TimelineFocusKind.DeletedRun)
        {
            if (markers.Count == 0)
                return current;
            var time = TimeOf(deletedRuns, current.Id) ?? TimeSpan.Zero;
            var id = NearestInLane(markers, time);
            return id is null
                ? current
                : new TimelineFocusTarget(TimelineFocusKind.Marker, id.Value);
        }

        return PickDefault(markers, deletedRuns);
    }

    /// <summary>Nearest marker to <paramref name="time"/>, else nearest deleted run, else none.</summary>
    public static TimelineFocusTarget NearestToTime(
        TimeSpan time,
        IReadOnlyList<(Guid Id, TimeSpan Time)> markers,
        IReadOnlyList<(Guid Id, TimeSpan Time)> deletedRuns)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(deletedRuns);

        if (NearestInLane(markers, time) is { } markerId)
            return new TimelineFocusTarget(TimelineFocusKind.Marker, markerId);
        if (NearestInLane(deletedRuns, time) is { } runId)
            return new TimelineFocusTarget(TimelineFocusKind.DeletedRun, runId);
        return TimelineFocusTarget.None;
    }

    /// <summary>
    /// After the focused item disappears: nearest remaining in the same lane,
    /// else the other lane, else none.
    /// </summary>
    public static TimelineFocusTarget AfterRemoval(
        TimelineFocusTarget removed,
        TimeSpan removedTime,
        IReadOnlyList<(Guid Id, TimeSpan Time)> markers,
        IReadOnlyList<(Guid Id, TimeSpan Time)> deletedRuns)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(deletedRuns);

        var sameLane = removed.Kind == TimelineFocusKind.Marker ? markers : deletedRuns;
        var otherLane = removed.Kind == TimelineFocusKind.Marker ? deletedRuns : markers;
        var sameKind = removed.Kind == TimelineFocusKind.Marker
            ? TimelineFocusKind.Marker
            : TimelineFocusKind.DeletedRun;
        var otherKind = removed.Kind == TimelineFocusKind.Marker
            ? TimelineFocusKind.DeletedRun
            : TimelineFocusKind.Marker;

        if (NearestInLane(sameLane, removedTime) is { } sameId)
            return new TimelineFocusTarget(sameKind, sameId);
        if (NearestInLane(otherLane, removedTime) is { } otherId)
            return new TimelineFocusTarget(otherKind, otherId);
        return TimelineFocusTarget.None;
    }

    public static TimelineFocusTarget PickDefault(
        IReadOnlyList<(Guid Id, TimeSpan Time)> markers,
        IReadOnlyList<(Guid Id, TimeSpan Time)> deletedRuns)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(deletedRuns);

        if (markers.Count > 0)
            return new TimelineFocusTarget(TimelineFocusKind.Marker, markers[0].Id);
        if (deletedRuns.Count > 0)
            return new TimelineFocusTarget(TimelineFocusKind.DeletedRun, deletedRuns[0].Id);
        return TimelineFocusTarget.None;
    }

    /// <summary>Nearest id by absolute time distance; ties prefer the earlier item.</summary>
    public static Guid? NearestInLane(
        IReadOnlyList<(Guid Id, TimeSpan Time)> lane,
        TimeSpan time)
    {
        ArgumentNullException.ThrowIfNull(lane);
        if (lane.Count == 0)
            return null;

        Guid? bestId = null;
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

    private static int IndexOf(IReadOnlyList<(Guid Id, TimeSpan Time)> lane, Guid id)
    {
        for (var i = 0; i < lane.Count; i++)
        {
            if (lane[i].Id == id)
                return i;
        }

        return -1;
    }

    private static TimeSpan? TimeOf(IReadOnlyList<(Guid Id, TimeSpan Time)> lane, Guid id)
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
