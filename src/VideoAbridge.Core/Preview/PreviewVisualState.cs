namespace VideoAbridge.Core.Preview;

/// <summary>
/// Realtime preview frame state: playhead, cut jumps, crossfade blend, and fade overlays.
/// </summary>
public readonly record struct PreviewVisualState(
    TimeSpan PlayheadSourceTime,
    TimeSpan AbridgedTime,
    double BlackoutOpacity,
    bool InCrossfade,
    TimeSpan IncomingSourceTime,
    double IncomingOpacity,
    TimeSpan? JumpPrimaryTo,
    bool PreviewComplete)
{
    /// <summary>~1 frame at 24fps — avoid TimeSpan tick noise around cut boundaries.</summary>
    public const double FrameEpsilonSeconds = 1.0 / 24.0;

    /// <summary>
    /// Evaluates preview visuals for the primary player's source time against an export plan.
    /// </summary>
    public static PreviewVisualState Evaluate(ExportPlan plan, PreviewTimeMap map, TimeSpan primarySourceTime)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(map);

        var clips = plan.Clips;
        if (clips.Count == 0)
        {
            return new PreviewVisualState(
                TimeSpan.Zero,
                TimeSpan.Zero,
                1,
                false,
                TimeSpan.Zero,
                0,
                null,
                PreviewComplete: true);
        }

        // Work in seconds to avoid TimeSpan tick/rounding chatter at cut edges.
        var t = primarySourceTime.TotalSeconds;
        var total = map.AbridgedDuration;
        const double eps = FrameEpsilonSeconds;

        for (var i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            var outgoingXf = i < plan.JoinsBetweenClips.Count
                && plan.JoinsBetweenClips[i].Mode == TransitionMode.Crossfade
                && plan.JoinsBetweenClips[i].Crossfade > TimeSpan.Zero
                    ? plan.JoinsBetweenClips[i].Crossfade.TotalSeconds
                    : 0;
            var incomingXf = i > 0
                && plan.JoinsBetweenClips[i - 1].Mode == TransitionMode.Crossfade
                && plan.JoinsBetweenClips[i - 1].Crossfade > TimeSpan.Zero
                    ? plan.JoinsBetweenClips[i - 1].Crossfade.TotalSeconds
                    : 0;

            var clipStart = clip.Start.TotalSeconds;
            var clipEnd = clip.End.TotalSeconds;
            var soloStart = clipStart + incomingXf;
            var soloEnd = clipEnd - outgoingXf;
            if (soloEnd < soloStart)
                soloEnd = soloStart;

            var nextPlayable = i + 1 < clips.Count
                ? clips[i + 1].Start.TotalSeconds + outgoingXf
                : double.PositiveInfinity;

            // Solo region of this clip (exclusive of the cut/xf boundary).
            if (t >= soloStart - eps && t < soloEnd - eps)
            {
                var playhead = TimeSpan.FromSeconds(t);
                var abridged = map.ToAbridged(playhead);
                return new PreviewVisualState(
                    playhead,
                    abridged,
                    BlackoutOpacityFor(plan, abridged, total),
                    false,
                    TimeSpan.Zero,
                    0,
                    null,
                    PreviewComplete: false);
            }

            // Crossfade tail of this clip. Use a half-open [soloEnd, clipEnd) without carving off
            // FrameEpsilon from the end — that aborted the dissolve one frame early and flickered.
            if (outgoingXf > 0 && t >= soloEnd - eps && t < clipEnd)
            {
                var u = (t - soloEnd) / outgoingXf;
                u = Math.Clamp(u, 0, 1);
                var next = clips[i + 1];
                var incomingTime = TimeSpan.FromSeconds(next.Start.TotalSeconds + u * outgoingXf);
                var playhead = TimeSpan.FromSeconds(
                    clipEnd + u * (next.Start.TotalSeconds - clipEnd));
                var abridged = map.ToAbridged(playhead);
                return new PreviewVisualState(
                    playhead,
                    abridged,
                    BlackoutOpacityFor(plan, abridged, total),
                    true,
                    incomingTime,
                    u,
                    null,
                    PreviewComplete: false);
            }

            // Deleted gap (or cut boundary): jump forward into the next kept section.
            // After a crossfade, jump only once past clipEnd (the outgoing tail is done).
            // Critical: only when we have NOT already reached nextPlayable — otherwise a later
            // clip's playback would keep matching "past end of earlier clip" and snap back.
            var jumpAfter = outgoingXf > 0 ? clipEnd : soloEnd;
            var jumpEps = outgoingXf > 0 ? 1e-9 : eps;
            if (i + 1 < clips.Count && t >= jumpAfter - jumpEps && t < nextPlayable - eps)
            {
                var jumpTo = TimeSpan.FromSeconds(nextPlayable);
                return new PreviewVisualState(
                    jumpTo,
                    map.ToAbridged(jumpTo),
                    BlackoutOpacityFor(plan, map.ToAbridged(jumpTo), total),
                    false,
                    TimeSpan.Zero,
                    0,
                    jumpTo,
                    PreviewComplete: false);
            }

            // Past the final clip.
            if (i + 1 >= clips.Count && t >= soloEnd - eps)
            {
                var end = TimeSpan.FromSeconds(clipEnd);
                return new PreviewVisualState(
                    end,
                    total,
                    BlackoutOpacityFor(plan, total, total),
                    false,
                    TimeSpan.Zero,
                    0,
                    null,
                    PreviewComplete: true);
            }
        }

        // Before the first clip (leading delete) — jump to first playable.
        var first = clips[0];
        var firstPlayable = first.Start.TotalSeconds;
        if (t < firstPlayable - eps)
        {
            var jumpTo = TimeSpan.FromSeconds(firstPlayable);
            return new PreviewVisualState(
                jumpTo,
                map.ToAbridged(jumpTo),
                BlackoutOpacityFor(plan, map.ToAbridged(jumpTo), total),
                false,
                TimeSpan.Zero,
                0,
                jumpTo,
                PreviewComplete: false);
        }

        // Fallback: already inside some kept region (tolerance) — don't jump.
        var playheadFallback = TimeSpan.FromSeconds(t);
        return new PreviewVisualState(
            playheadFallback,
            map.ToAbridged(playheadFallback),
            BlackoutOpacityFor(plan, map.ToAbridged(playheadFallback), total),
            false,
            TimeSpan.Zero,
            0,
            null,
            PreviewComplete: false);
    }

    /// <summary>Fade-to/from-black opacity (0 = fully audible, 1 = silent) at abridged time.</summary>
    public static double BlackoutOpacityFor(ExportPlan plan, TimeSpan abridged, TimeSpan total)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var t = abridged.TotalSeconds;
        var dur = Math.Max(total.TotalSeconds, 1e-6);
        var opacity = 0.0;

        if (plan.FadeFromBlackSeconds is > 0 and var fadeIn)
            opacity = Math.Max(opacity, 1.0 - Math.Clamp(t / fadeIn, 0, 1));

        if (plan.FadeToBlackSeconds is > 0 and var fadeOut)
        {
            var start = dur - fadeOut;
            opacity = Math.Max(opacity, Math.Clamp((t - start) / fadeOut, 0, 1));
        }

        return Math.Clamp(opacity, 0, 1);
    }
}
