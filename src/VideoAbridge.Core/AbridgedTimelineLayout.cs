namespace VideoAbridge.Core;

public enum AbridgedOverlayKind
{
    FadeIn,
    FadeOut,
    Crossfade,
    Cut
}

/// <summary>
/// Overlay on the abridged (export) timeline ruler. Cuts use Start == End;
/// fades/crossfades use a half-open [Start, End) in abridged time.
/// </summary>
public sealed record AbridgedOverlay(
    AbridgedOverlayKind Kind,
    TimeSpan Start,
    TimeSpan End,
    Guid? DeletedRunId);

/// <summary>
/// Abridged-timeline geometry for Preview mode: duration matches
/// <see cref="PreviewTimeMap"/>, with fade / crossfade / cut overlays.
/// </summary>
public sealed class AbridgedTimelineLayout
{
    private AbridgedTimelineLayout(TimeSpan duration, IReadOnlyList<AbridgedOverlay> overlays)
    {
        Duration = duration;
        Overlays = overlays;
    }

    public TimeSpan Duration { get; }
    public IReadOnlyList<AbridgedOverlay> Overlays { get; }

    public static AbridgedTimelineLayout Empty { get; } = new(TimeSpan.Zero, []);

    /// <summary>
    /// Builds layout from an export plan. <paramref name="joinDeletedRunIds"/> must
    /// align 1:1 with <see cref="ExportPlan.JoinsBetweenClips"/> (interior deleted runs).
    /// </summary>
    public static AbridgedTimelineLayout FromPlan(
        ExportPlan plan,
        IReadOnlyList<Guid>? joinDeletedRunIds = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Clips.Count == 0)
            return Empty;

        var overlays = new List<AbridgedOverlay>();
        var outPos = 0.0;
        var clips = plan.Clips;
        var joins = plan.JoinsBetweenClips;

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
                : (Guid?)null;

            var join = joins[i];
            if (join.Mode == TransitionMode.Crossfade && join.CrossfadeSeconds > 0)
            {
                var xf = join.CrossfadeSeconds;
                overlays.Add(new AbridgedOverlay(
                    AbridgedOverlayKind.Crossfade,
                    TimeSpan.FromSeconds(outPos),
                    TimeSpan.FromSeconds(outPos + xf),
                    runId));
                outPos += xf;
            }
            else
            {
                var at = TimeSpan.FromSeconds(outPos);
                overlays.Add(new AbridgedOverlay(AbridgedOverlayKind.Cut, at, at, runId));
            }
        }

        var duration = TimeSpan.FromSeconds(outPos);

        if (plan.FadeFromBlackAtStart && plan.FadeFromBlackSeconds > 0 && duration > TimeSpan.Zero)
        {
            var fade = Math.Min(plan.FadeFromBlackSeconds, duration.TotalSeconds);
            overlays.Insert(0, new AbridgedOverlay(
                AbridgedOverlayKind.FadeIn,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(fade),
                DeletedRunId: null));
        }

        if (plan.FadeToBlackAtEnd && plan.FadeToBlackSeconds > 0 && duration > TimeSpan.Zero)
        {
            var fade = Math.Min(plan.FadeToBlackSeconds, duration.TotalSeconds);
            overlays.Add(new AbridgedOverlay(
                AbridgedOverlayKind.FadeOut,
                TimeSpan.FromSeconds(Math.Max(0, duration.TotalSeconds - fade)),
                duration,
                DeletedRunId: null));
        }

        return new AbridgedTimelineLayout(duration, overlays);
    }

    public static AbridgedTimelineLayout FromTimeline(Timeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.TryBuildExportPlan() is not Ok<ExportPlan> plan)
            return Empty;

        List<Guid> runIds =
        [
            .. timeline.GetDeletedRuns().Where(r => r.IsInterior).Select(r => r.Id)
        ];

        return FromPlan(plan.Value, runIds);
    }
}
