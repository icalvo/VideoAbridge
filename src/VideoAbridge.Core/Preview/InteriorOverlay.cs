namespace VideoAbridge.Core.Preview;

public readonly record struct InteriorOverlay(
    TransitionMode Mode,
    TimeRange Range,
    Guid DeletedRunId);
