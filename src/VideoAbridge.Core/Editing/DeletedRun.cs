namespace VideoAbridge.Core.Editing;

public sealed record DeletedRun(
    DeletedRunId Id,
    TimeRange Range,
    bool IsInterior,
    JoinTransition? Join);
