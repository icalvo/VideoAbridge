namespace VideoAbridge.Core;

public enum TransitionMode
{
    HardCut,
    Crossfade
}

public sealed record JoinTransition(
    Guid DeletedRunId,
    TransitionMode Mode,
    double CrossfadeSeconds);
