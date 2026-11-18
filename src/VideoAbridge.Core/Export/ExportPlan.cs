using System.Collections.Immutable;

namespace VideoAbridge.Core.Export;

/// <summary>
/// Ordered kept clips with the join that connects clip[i] to clip[i+1].
/// </summary>
public sealed record ExportPlan(
    ImmutableList<TimeRange> Clips,
    ImmutableList<ExportJoin> JoinsBetweenClips,
    double? FadeFromBlackSeconds,
    double? FadeToBlackSeconds)
{
    public bool FadeFromBlackAtStart => FadeFromBlackSeconds is not null;

    public bool FadeToBlackAtEnd => FadeToBlackSeconds is not null;
}
