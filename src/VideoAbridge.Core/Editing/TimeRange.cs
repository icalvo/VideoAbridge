namespace VideoAbridge.Core.Editing;

/// <summary>
/// Inclusive time interval [Start, End] on a timeline (source or abridged).
/// </summary>
public readonly record struct TimeRange(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;

    public bool IsEmpty => End <= Start;

    public bool IsPoint => End == Start;

    /// <summary>Inclusive containment: <paramref name="time"/> is in [Start, End].</summary>
    public bool Contains(TimeSpan time) => time >= Start && time <= End;

    /// <summary>True when the open intervals (Start, End) and (other.Start, other.End) overlap.</summary>
    public bool Overlaps(TimeRange other) => Start < other.End && other.Start < End;

    /// <summary>Strictly inside (0, <paramref name="timelineDuration"/>).</summary>
    public bool IsInteriorOf(TimeSpan timelineDuration) =>
        Start > TimeSpan.Zero && End < timelineDuration;

    /// <summary>Covers the full timeline from t=0 through <paramref name="timelineDuration"/>.</summary>
    public bool Covers(TimeSpan timelineDuration) =>
        Start <= TimeSpan.Zero && End >= timelineDuration;

    public bool TouchesStart => Start <= TimeSpan.Zero;

    public bool TouchesEnd(TimeSpan timelineDuration) => End >= timelineDuration;

    public TimeSpan AbsDistanceTo(TimeSpan time)
    {
        if (Contains(time))
            return TimeSpan.Zero;
        return time < Start ? Start - time : time - End;
    }

    public TimeSpan NearestEdge(TimeSpan time)
    {
        var toStart = time > Start ? time - Start : Start - time;
        var toEnd = time > End ? time - End : End - time;
        return toStart <= toEnd ? Start : End;
    }

    public override string ToString() => $"[{Start}, {End}]";
}
