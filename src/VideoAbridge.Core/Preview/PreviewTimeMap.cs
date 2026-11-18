using System.Collections.Immutable;

namespace VideoAbridge.Core.Preview;

/// <summary>
/// Maps between abridged (export) timeline time and source timeline time,
/// matching the FFmpeg concat/xfade filter graph used for export.
/// </summary>
public sealed class PreviewTimeMap
{
    private readonly ImmutableList<Segment> _segments;

    private PreviewTimeMap(ImmutableList<Segment> segments, TimeSpan abridgedDuration)
    {
        _segments = segments;
        AbridgedDuration = abridgedDuration;
    }

    public TimeSpan AbridgedDuration { get; }

    public static PreviewTimeMap Empty { get; } = new([], TimeSpan.Zero);

    public static PreviewTimeMap FromPlan(ExportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Clips.Count == 0)
            return Empty;

        var segments = ImmutableList.CreateBuilder<Segment>();
        var outPos = 0.0;
        var clips = plan.Clips;
        var joins = plan.JoinsBetweenClips;

        // Indexed walk: each clip couples to join[i] / join[i-1].
        for (var i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            var incomingXf = i > 0
                && i - 1 < joins.Count
                && joins[i - 1].Mode == TransitionMode.Crossfade
                && joins[i - 1].CrossfadeSeconds > 0
                    ? joins[i - 1].CrossfadeSeconds
                    : 0;
            var outgoingXf = i < joins.Count
                && joins[i].Mode == TransitionMode.Crossfade
                && joins[i].CrossfadeSeconds > 0
                    ? joins[i].CrossfadeSeconds
                    : 0;

            var srcStart = clip.Start.TotalSeconds + incomingXf;
            var solo = clip.Duration.TotalSeconds - incomingXf - outgoingXf;
            if (solo < 0)
                solo = 0;

            if (solo > 1e-9)
            {
                var srcEnd = srcStart + solo;
                segments.Add(new Segment(outPos, outPos + solo, srcStart, srcEnd));
                outPos += solo;
            }

            if (outgoingXf > 0 && i + 1 < clips.Count)
            {
                var next = clips[i + 1];
                // During crossfade, move the source playhead across the deleted gap.
                segments.Add(new Segment(
                    outPos,
                    outPos + outgoingXf,
                    clip.End.TotalSeconds,
                    next.Start.TotalSeconds));
                outPos += outgoingXf;
            }
        }

        return new PreviewTimeMap(segments.ToImmutable(), TimeSpan.FromSeconds(outPos));
    }

    public TimeSpan ToSource(TimeSpan abridgedTime)
    {
        if (_segments.Count == 0)
            return TimeSpan.Zero;

        var t = abridgedTime.TotalSeconds;
        if (t <= _segments[0].OutStart)
            return TimeSpan.FromSeconds(_segments[0].SrcStart);

        for (var i = 0; i < _segments.Count; i++)
        {
            var seg = _segments[i];
            var isLast = i == _segments.Count - 1;
            if (t < seg.OutEnd || (isLast && t <= seg.OutEnd + 1e-9))
            {
                if (t < seg.OutStart)
                    return TimeSpan.FromSeconds(seg.SrcStart);

                var span = seg.OutEnd - seg.OutStart;
                if (span <= 1e-12)
                    return TimeSpan.FromSeconds(seg.SrcStart);

                var u = (t - seg.OutStart) / span;
                u = Math.Clamp(u, 0, 1);
                return TimeSpan.FromSeconds(seg.SrcStart + u * (seg.SrcEnd - seg.SrcStart));
            }
        }

        return TimeSpan.FromSeconds(_segments[^1].SrcEnd);
    }

    public TimeSpan ToAbridged(TimeSpan sourceTime)
    {
        if (_segments.Count == 0)
            return TimeSpan.Zero;

        var s = sourceTime.TotalSeconds;
        var hit = _segments
            .Select(seg =>
            {
                var srcMin = Math.Min(seg.SrcStart, seg.SrcEnd);
                var srcMax = Math.Max(seg.SrcStart, seg.SrcEnd);
                if (s < srcMin - 1e-9 || s > srcMax + 1e-9)
                    return (Hit: false, Out: 0.0);

                var srcSpan = seg.SrcEnd - seg.SrcStart;
                if (Math.Abs(srcSpan) <= 1e-12)
                    return (Hit: true, Out: seg.OutStart);

                var u = Math.Clamp((s - seg.SrcStart) / srcSpan, 0, 1);
                return (Hit: true, Out: seg.OutStart + u * (seg.OutEnd - seg.OutStart));
            })
            .FirstOrDefault(x => x.Hit);

        if (hit.Hit)
            return TimeSpan.FromSeconds(hit.Out);

        var nearest = _segments
            .SelectMany(seg => ((double[])[seg.SrcStart, seg.SrcEnd]).Select(edge => (
                Dist: Math.Abs(s - edge),
                Out: Math.Abs(edge - seg.SrcStart) <= Math.Abs(edge - seg.SrcEnd)
                    ? seg.OutStart
                    : seg.OutEnd)))
            .Aggregate(
                (Dist: double.MaxValue, Out: _segments[0].OutStart),
                (best, candidate) => candidate.Dist < best.Dist ? candidate : best);

        return TimeSpan.FromSeconds(nearest.Out);
    }

    private readonly record struct Segment(
        double OutStart,
        double OutEnd,
        double SrcStart,
        double SrcEnd);
}
