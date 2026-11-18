namespace VideoAbridge.Core.Editing;

public sealed record JoinTransition(
    Guid DeletedRunId,
    TransitionMode Mode,
    double CrossfadeSeconds);
