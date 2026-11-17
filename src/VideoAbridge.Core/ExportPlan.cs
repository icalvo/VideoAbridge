namespace VideoAbridge.Core;

public sealed record KeptClip(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;
}

public sealed record ExportJoin(
    TransitionMode Mode,
    double CrossfadeSeconds);

/// <summary>
/// Ordered kept clips with the join that connects clip[i] to clip[i+1].
/// </summary>
public sealed record ExportPlan(
    IReadOnlyList<KeptClip> Clips,
    IReadOnlyList<ExportJoin> JoinsBetweenClips,
    bool FadeFromBlackAtStart,
    double FadeFromBlackSeconds,
    bool FadeToBlackAtEnd,
    double FadeToBlackSeconds);
