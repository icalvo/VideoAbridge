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
        ImmutableList<DeletedRunId>? joinDeletedRunIds = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Clips.Count == 0)
            return Empty;

        var overlays = ImmutableList.CreateBuilder<AbridgedOverlay>();
        var outPos = TimeSpan.Zero;
        var clips = plan.Clips;
        var joins = plan.JoinsBetweenClips;

        // Indexed walk: each clip couples to join[i] / join[i-1].
        for (var i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            var incomingXf = i > 0
                && i - 1 < joins.Count
                && joins[i - 1].Mode == TransitionMode.Crossfade
                && joins[i - 1].Crossfade > TimeSpan.Zero
                    ? joins[i - 1].Crossfade
                    : TimeSpan.Zero;
            var outgoingXf = i < joins.Count
                && joins[i].Mode == TransitionMode.Crossfade
                && joins[i].Crossfade > TimeSpan.Zero
                    ? joins[i].Crossfade
                    : TimeSpan.Zero;

            var solo = clip.Duration - incomingXf - outgoingXf;
            if (solo < TimeSpan.Zero)
                solo = TimeSpan.Zero;

            outPos += solo;

            if (i >= joins.Count || i + 1 >= clips.Count)
                continue;

            var runId = joinDeletedRunIds is not null && i < joinDeletedRunIds.Count
                ? joinDeletedRunIds[i]
                : default;

            var join = joins[i];
            if (join.Mode == TransitionMode.Crossfade && join.Crossfade > TimeSpan.Zero)
            {
                var xf = join.Crossfade;
                overlays.Add(new InteriorOverlay(
                    TransitionMode.Crossfade,
                    new TimeRange(outPos, outPos + xf),
                    runId));
                outPos += xf;
            }
            else
            {
                overlays.Add(new InteriorOverlay(
                    TransitionMode.HardCut,
                    new TimeRange(outPos, outPos),
                    runId));
            }
        }

        var duration = outPos;
        var list = overlays.ToImmutable();

        if (plan.FadeFromBlackSeconds is > 0 && duration > TimeSpan.Zero)
        {
            var fade = TimeSpans.Min(TimeSpan.FromSeconds(plan.FadeFromBlackSeconds.Value), duration);
            list = list.Insert(0, new ExteriorOverlay(
                ExteriorOverlayKind.FadeIn,
                new TimeRange(TimeSpan.Zero, fade)));
        }

        if (plan.FadeToBlackSeconds is > 0 && duration > TimeSpan.Zero)
        {
            var fade = TimeSpans.Min(TimeSpan.FromSeconds(plan.FadeToBlackSeconds.Value), duration);
            list = list.Add(new ExteriorOverlay(
                ExteriorOverlayKind.FadeOut,
                new TimeRange(duration - fade, duration)));
        }

        return new AbridgedTimelineLayout(duration, list);
    }

    public static AbridgedTimelineLayout FromTimeline(Timeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var (kept, deleted) = timeline.GetKeptAndDeletedRuns();
        return timeline.TryBuildExportPlan(kept, deleted).Match(
            plan =>
            {
                ImmutableList<DeletedRunId> runIds =
                [
                    .. deleted.Where(r => r.IsInterior).Select(r => r.Id)
                ];
                return FromPlan(plan, runIds);
            },
            _ => Empty);
    }
}
