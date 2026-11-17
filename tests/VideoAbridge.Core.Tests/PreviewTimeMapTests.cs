using FluentAssertions;
using VideoAbridge.Core;

namespace VideoAbridge.Core.Tests;

public class PreviewTimeMapTests
{
    [Fact]
    public void HardCutMapsClipsSequentiallyAndJumpsDeletedGap()
    {
        var plan = new ExportPlan(
            [
                new KeptClip(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(10)),
                new KeptClip(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30))
            ],
            [new ExportJoin(TransitionMode.HardCut, 0)],
            FadeFromBlackAtStart: false,
            FadeFromBlackSeconds: 0,
            FadeToBlackAtEnd: false,
            FadeToBlackSeconds: 0);

        var map = PreviewTimeMap.FromPlan(plan);
        map.AbridgedDuration.TotalSeconds.Should().BeApproximately(20, 1e-6);

        map.ToSource(TimeSpan.FromSeconds(5)).TotalSeconds.Should().BeApproximately(5, 1e-6);
        map.ToSource(TimeSpan.FromSeconds(10)).TotalSeconds.Should().BeApproximately(20, 1e-6);
        map.ToSource(TimeSpan.FromSeconds(15)).TotalSeconds.Should().BeApproximately(25, 1e-6);

        map.ToAbridged(TimeSpan.FromSeconds(25)).TotalSeconds.Should().BeApproximately(15, 1e-6);
        map.ToAbridged(TimeSpan.FromSeconds(5)).TotalSeconds.Should().BeApproximately(5, 1e-6);
    }

    [Fact]
    public void CrossfadeSweepsSourceAcrossDeletedGap()
    {
        var plan = new ExportPlan(
            [
                new KeptClip(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(10)),
                new KeptClip(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30))
            ],
            [new ExportJoin(TransitionMode.Crossfade, 2)],
            FadeFromBlackAtStart: false,
            FadeFromBlackSeconds: 0,
            FadeToBlackAtEnd: false,
            FadeToBlackSeconds: 0);

        var map = PreviewTimeMap.FromPlan(plan);
        map.AbridgedDuration.TotalSeconds.Should().BeApproximately(18, 1e-6);

        // Solo first clip (0..8) → source 0..8
        map.ToSource(TimeSpan.FromSeconds(4)).TotalSeconds.Should().BeApproximately(4, 1e-6);

        // Mid-crossfade (out=9) → halfway across deleted 10..20
        map.ToSource(TimeSpan.FromSeconds(9)).TotalSeconds.Should().BeApproximately(15, 1e-6);

        // Solo second clip starts at out=10 → source 22
        map.ToSource(TimeSpan.FromSeconds(10)).TotalSeconds.Should().BeApproximately(22, 1e-6);
    }
}
