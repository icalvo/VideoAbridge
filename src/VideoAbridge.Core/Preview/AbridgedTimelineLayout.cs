using System.Collections.Immutable;

namespace VideoAbridge.Core.Preview;

/// <summary>
/// Abridged-timeline geometry for Preview mode: duration matches
/// <see cref="PreviewTimeMap"/>, with fade / crossfade / cut overlays.
/// </summary>
public sealed class AbridgedTimelineLayout
{
    private AbridgedTimelineLayout(TimeSpan duration, ImmutableList<AbridgedOverlay> overlays)
    {
        Duration = duration;
        Overlays = overlays;
    }

    public TimeSpan Duration { get; }
    public ImmutableList<AbridgedOverlay> Overlays { get; }

    public static AbridgedTimelineLayout Empty { get; } = new(TimeSpan.Zero, []);

    /// <summary>
    /// Builds layout from an export plan. <paramref name="joinDeletedRunIds"/> must
    /// align 1:1 with <see cref="ExportPlan.JoinsBetweenClips"/> (interior deleted runs).
    /// </summary>
    public static AbridgedTimelineLayout FromPlan(
        ExportPlan plan,
        ImmutableList<Guid>? joinDeletedRunIds = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Clips.Count == 0)
            return Empty;

        var overlays = ImmutableList.CreateBuilder<AbridgedOverlay>();
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

            var solo = clip.Duration.TotalSeconds - incomingXf - outgoingXf;
            if (solo < 0)
                solo = 0;

            outPos += solo;

            if (i >= joins.Count || i + 1 >= clips.Count)
                continue;

            var runId = joinDeletedRunIds is not null && i < joinDeletedRunIds.Count
                ? joinDeletedRunIds[i]
                : Guid.Empty;

            var join = joins[i];
            if (join.Mode == TransitionMode.Crossfade && join.CrossfadeSeconds > 0)
            {
                var xf = join.CrossfadeSeconds;
                overlays.Add(new InteriorOverlay(
                    TransitionMode.Crossfade,
                    new TimeRange(TimeSpan.FromSeconds(outPos), TimeSpan.FromSeconds(outPos + xf)),
                    runId));
                outPos += xf;
            }
            else
            {
                var at = TimeSpan.FromSeconds(outPos);
                overlays.Add(new InteriorOverlay(
                    TransitionMode.HardCut,
                    new TimeRange(at, at),
                    runId));
            }
        }

        var duration = TimeSpan.FromSeconds(outPos);
        var list = overlays.ToImmutable();

        if (plan.FadeFromBlackSeconds is > 0 && duration > TimeSpan.Zero)
        {
            var fade = Math.Min(plan.FadeFromBlackSeconds.Value, duration.TotalSeconds);
            list = list.Insert(0, new ExteriorOverlay(
                ExteriorOverlayKind.FadeIn,
                new TimeRange(TimeSpan.Zero, TimeSpan.FromSeconds(fade))));
        }

        if (plan.FadeToBlackSeconds is > 0 && duration > TimeSpan.Zero)
        {
            var fade = Math.Min(plan.FadeToBlackSeconds.Value, duration.TotalSeconds);
            list = list.Add(new ExteriorOverlay(
                ExteriorOverlayKind.FadeOut,
                new TimeRange(
                    TimeSpan.FromSeconds(Math.Max(0, duration.TotalSeconds - fade)),
                    duration)));
        }

        return new AbridgedTimelineLayout(duration, list);
    }

    public static AbridgedTimelineLayout FromTimeline(Timeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return timeline.TryBuildExportPlan().Match(
            plan =>
            {
                ImmutableList<Guid> runIds =
                [
                    .. timeline.GetDeletedRuns().Where(r => r.IsInterior).Select(r => r.Id)
                ];
                return FromPlan(plan, runIds);
            },
            _ => Empty);
    }
}
