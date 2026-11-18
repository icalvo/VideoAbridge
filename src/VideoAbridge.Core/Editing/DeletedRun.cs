namespace VideoAbridge.Core.Editing;

public sealed record DeletedRun(
    Guid Id,
    TimeRange Range,
    bool IsInterior,
    JoinTransition? Join);
