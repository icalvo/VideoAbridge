namespace VideoAbridge.Core;

public sealed record TimeRange(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;
    public bool Overlaps(TimeRange other) => Start < other.End && other.Start < End;
}

public sealed record DeletedRun(
    Guid Id,
    TimeSpan Start,
    TimeSpan End,
    bool IsInterior,
    JoinTransition? Join);

public sealed record KeptRun(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;
}
