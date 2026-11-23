namespace VideoAbridge.Core.Editing;

public sealed record JoinTransition(
    DeletedRunId DeletedRunId,
    TransitionMode Mode,
    TimeSpan Crossfade);
