namespace VideoAbridge.Core.Preview;

/// <summary>
/// Dual-player seek target for an abridged (export-clock) time: primary on the
/// outgoing kept tail during a crossfade, secondary on the incoming head.
/// </summary>
public readonly record struct PreviewSeekState(
    TimeSpan AbridgedTime,
    TimeSpan PrimarySourceTime,
    TimeSpan PlayheadSourceTime,
    bool InCrossfade,
    TimeSpan IncomingSourceTime,
    double IncomingOpacity,
    double BlackoutOpacity)
{
    /// <summary>
    /// Maps abridged timeline time to primary/secondary source times for Preview seek/scrub.
    /// During a crossfade, primary parks on the outgoing kept tail (not the deleted gap).
    /// </summary>
    public static PreviewSeekState FromAbridged(ExportPlan plan, PreviewTimeMap map, TimeSpan abridgedTime)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(map);

        var clips = plan.Clips;
        if (clips.Count == 0 || map.AbridgedDuration <= TimeSpan.Zero)
        {
            return new PreviewSeekState(
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                false,
                TimeSpan.Zero,
                0,
                1);
        }

        var total = map.AbridgedDuration;
        var t = abridgedTime.TotalSeconds;
        if (t < 0)
            t = 0;
        else if (t > total.TotalSeconds)
            t = total.TotalSeconds;

        var joins = plan.JoinsBetweenClips;
        var outPos = 0.0;

        for (var i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            var incomingXf = i > 0
                && i - 1 < joins.Count
                && joins[i - 1].Mode == TransitionMode.Crossfade
                && joins[i - 1].Crossfade > TimeSpan.Zero
                    ? joins[i - 1].Crossfade.TotalSeconds
                    : 0;
            var outgoingXf = i < joins.Count
                && joins[i].Mode == TransitionMode.Crossfade
                && joins[i].Crossfade > TimeSpan.Zero
                    ? joins[i].Crossfade.TotalSeconds
                    : 0;

            var srcStart = clip.Start.TotalSeconds + incomingXf;
            var solo = clip.Duration.TotalSeconds - incomingXf - outgoingXf;
            if (solo < 0)
                solo = 0;

            if (solo > 1e-9)
            {
                var soloEnd = outPos + solo;
                if (t < soloEnd - 1e-12 || (i + 1 >= clips.Count && outgoingXf <= 0 && t <= soloEnd + 1e-9))
                {
                    var uSolo = Math.Clamp((t - outPos) / solo, 0, 1);
                    var primary = TimeSpan.FromSeconds(srcStart + uSolo * solo);
                    var abridged = TimeSpan.FromSeconds(t);
                    return new PreviewSeekState(
                        abridged,
                        primary,
                        primary,
                        false,
                        TimeSpan.Zero,
                        0,
                        PreviewVisualState.BlackoutOpacityFor(plan, abridged, total));
                }

                outPos = soloEnd;
            }

            if (outgoingXf > 0 && i + 1 < clips.Count)
            {
                var xfEnd = outPos + outgoingXf;
                // Half-open [outPos, xfEnd): xfEnd belongs to the next clip's solo start.
                if (t < xfEnd - 1e-12)
                {
                    var u = Math.Clamp((t - outPos) / outgoingXf, 0, 1);
                    var next = clips[i + 1];
                    var soloEndSrc = clip.End.TotalSeconds - outgoingXf;
                    var primary = TimeSpan.FromSeconds(soloEndSrc + u * outgoingXf);
                    var incoming = TimeSpan.FromSeconds(next.Start.TotalSeconds + u * outgoingXf);
                    var playhead = TimeSpan.FromSeconds(
                        clip.End.TotalSeconds + u * (next.Start.TotalSeconds - clip.End.TotalSeconds));
                    var abridged = TimeSpan.FromSeconds(t);
                    return new PreviewSeekState(
                        abridged,
                        primary,
                        playhead,
                        true,
                        incoming,
                        u,
                        PreviewVisualState.BlackoutOpacityFor(plan, abridged, total));
                }

                outPos = xfEnd;
            }
        }

        // Past end — park on last clip end (kept).
        var endPrimary = TimeSpan.FromSeconds(clips[^1].End.TotalSeconds);
        return new PreviewSeekState(
            total,
            endPrimary,
            endPrimary,
            false,
            TimeSpan.Zero,
            0,
            PreviewVisualState.BlackoutOpacityFor(plan, total, total));
    }
}
