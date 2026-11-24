namespace VideoAbridge.Core.Preview;

/// <summary>
/// Preview-mode Left/Right: seek targets sit a lead-in before each cut or non-zero fade range
/// so the transition is visible when playback continues.
/// </summary>
public static class PreviewOverlayNavigation
{
    public static readonly TimeSpan LeadIn = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Distinct seek times (ascending): <c>max(0, range.Start - LeadIn)</c> for each qualifying overlay.
    /// Hard cuts always qualify; fades/crossfades only when their range duration is positive.
    /// </summary>
    public static IReadOnlyList<TimeSpan> SeekTargets(IReadOnlyList<AbridgedOverlay> overlays)
    {
        ArgumentNullException.ThrowIfNull(overlays);
        if (overlays.Count == 0)
            return [];

        var set = new SortedSet<TimeSpan>();
        foreach (var overlay in overlays)
        {
            if (!Qualifies(overlay))
                continue;

            var start = overlay.Range.Start;
            var seek = start > LeadIn ? start - LeadIn : TimeSpan.Zero;
            set.Add(seek);
        }

        return [.. set];
    }

    public static TimeSpan? Previous(TimeSpan playhead, IReadOnlyList<TimeSpan> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        TimeSpan? best = null;
        foreach (var t in targets)
        {
            if (t >= playhead)
                break;
            best = t;
        }

        return best;
    }

    public static TimeSpan? Next(TimeSpan playhead, IReadOnlyList<TimeSpan> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        foreach (var t in targets)
        {
            if (t > playhead)
                return t;
        }

        return null;
    }

    private static bool Qualifies(AbridgedOverlay overlay) => overlay.Value switch
    {
        InteriorOverlay { Mode: TransitionMode.HardCut } => true,
        InteriorOverlay interior => interior.Range.Duration > TimeSpan.Zero,
        ExteriorOverlay exterior => exterior.Range.Duration > TimeSpan.Zero,
        _ => false,
    };
}
