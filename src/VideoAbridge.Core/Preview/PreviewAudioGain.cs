namespace VideoAbridge.Core.Preview;

/// <summary>
/// Source times and mix weights for drawing the Preview waveform under fades/crossfades.
/// </summary>
public readonly record struct PreviewAudioGain(
    double AudibleGain,
    TimeSpan PrimarySourceTime,
    double PrimaryWeight,
    TimeSpan SecondarySourceTime,
    double SecondaryWeight)
{
    /// <summary>
    /// Evaluates audible gain and source sample points for an abridged timeline position.
    /// Weights match playback: primary*(1-incoming) + secondary*incoming, then * audible.
    /// </summary>
    public static PreviewAudioGain Evaluate(ExportPlan plan, PreviewTimeMap map, TimeSpan abridgedTime)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(map);

        var total = map.AbridgedDuration;
        var audible = Math.Clamp(
            1.0 - PreviewVisualState.BlackoutOpacityFor(plan, abridgedTime, total),
            0,
            1);

        if (plan.Clips.Count == 0 || total <= TimeSpan.Zero)
        {
            return new PreviewAudioGain(0, TimeSpan.Zero, 1, TimeSpan.Zero, 0);
        }

        var t = Math.Clamp(abridgedTime.TotalSeconds, 0, total.TotalSeconds);
        var outPos = 0.0;
        var clips = plan.Clips;
        var joins = plan.JoinsBetweenClips;

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
                    var u = solo <= 1e-12 ? 0 : (t - outPos) / solo;
                    u = Math.Clamp(u, 0, 1);
                    var source = TimeSpan.FromSeconds(srcStart + u * solo);
                    return new PreviewAudioGain(audible, source, 1, TimeSpan.Zero, 0);
                }

                outPos = soloEnd;
            }

            if (outgoingXf > 0 && i + 1 < clips.Count)
            {
                var xfEnd = outPos + outgoingXf;
                if (t < xfEnd - 1e-12 || (i + 1 >= clips.Count - 0 && t <= xfEnd + 1e-9))
                {
                    var u = (t - outPos) / outgoingXf;
                    u = Math.Clamp(u, 0, 1);
                    var primary = TimeSpan.FromSeconds(clip.End.TotalSeconds - outgoingXf + u * outgoingXf);
                    var next = clips[i + 1];
                    var secondary = TimeSpan.FromSeconds(next.Start.TotalSeconds + u * outgoingXf);
                    return new PreviewAudioGain(audible, primary, 1.0 - u, secondary, u);
                }

                outPos = xfEnd;
            }
        }

        var last = clips[^1];
        return new PreviewAudioGain(audible, last.End, 1, TimeSpan.Zero, 0);
    }
}
