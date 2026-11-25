using FluentAssertions;

namespace VideoAbridge.Core.Tests;

public class PreviewSeekStateTests
{
    private static (ExportPlan Plan, PreviewTimeMap Map) CrossfadePlan()
    {
        var plan = new ExportPlan(
            [
                new TimeRange(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(10)),
                new TimeRange(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30))
            ],
            [new ExportJoin(TransitionMode.Crossfade, TimeSpan.FromSeconds(2))],
            FadeFromBlackSeconds: null,
            FadeToBlackSeconds: null);
        return (plan, PreviewTimeMap.FromPlan(plan));
    }

    [Fact]
    public void MidCrossfadeParksPrimaryOnOutgoingTail()
    {
        var (plan, map) = CrossfadePlan();
        // Abridged 9 = mid XF (solo 0..8, XF 8..10). u=0.5
        var seek = PreviewSeekState.FromAbridged(plan, map, TimeSpan.FromSeconds(9));

        seek.InCrossfade.Should().BeTrue();
        seek.IncomingOpacity.Should().BeApproximately(0.5, 1e-6);
        // Outgoing tail: 8..10 → mid at 9
        seek.PrimarySourceTime.TotalSeconds.Should().BeApproximately(9, 1e-6);
        // Incoming head: 20..22 → mid at 21
        seek.IncomingSourceTime.TotalSeconds.Should().BeApproximately(21, 1e-6);
        // Synthetic needle sweeps deleted gap 10..20
        seek.PlayheadSourceTime.TotalSeconds.Should().BeApproximately(15, 1e-6);
        seek.AbridgedTime.TotalSeconds.Should().BeApproximately(9, 1e-6);
    }

    [Fact]
    public void CrossfadeStartAndEndMatchEvaluateParity()
    {
        var (plan, map) = CrossfadePlan();

        var start = PreviewSeekState.FromAbridged(plan, map, TimeSpan.FromSeconds(8));
        start.InCrossfade.Should().BeTrue();
        start.IncomingOpacity.Should().BeApproximately(0, 1e-6);
        start.PrimarySourceTime.TotalSeconds.Should().BeApproximately(8, 1e-6);
        start.IncomingSourceTime.TotalSeconds.Should().BeApproximately(20, 1e-6);

        var end = PreviewSeekState.FromAbridged(plan, map, TimeSpan.FromSeconds(10));
        // At XF end / second-clip solo start: not in crossfade
        end.InCrossfade.Should().BeFalse();
        end.PrimarySourceTime.TotalSeconds.Should().BeApproximately(22, 1e-6);
    }

    [Fact]
    public void SoloRegionDoesNotEngageCrossfade()
    {
        var (plan, map) = CrossfadePlan();
        var seek = PreviewSeekState.FromAbridged(plan, map, TimeSpan.FromSeconds(4));
        seek.InCrossfade.Should().BeFalse();
        seek.IncomingOpacity.Should().Be(0);
        seek.PrimarySourceTime.TotalSeconds.Should().BeApproximately(4, 1e-6);
        seek.PlayheadSourceTime.Should().Be(seek.PrimarySourceTime);
    }

    [Fact]
    public void FrameStepsAcrossCrossfadeStayOnOutgoingTail()
    {
        var (plan, map) = CrossfadePlan();
        var frame = TimeSpan.FromSeconds(1.0 / 30.0);
        var prevPrimary = double.NegativeInfinity;

        for (var abridged = 7.5; abridged <= 10.0; abridged += frame.TotalSeconds)
        {
            var seek = PreviewSeekState.FromAbridged(plan, map, TimeSpan.FromSeconds(abridged));
            seek.AbridgedTime.TotalSeconds.Should().BeApproximately(abridged, 1e-6);

            if (abridged < 8 - 1e-9)
            {
                seek.InCrossfade.Should().BeFalse();
                seek.PrimarySourceTime.TotalSeconds.Should().BeApproximately(abridged, 1e-6);
            }
            else if (abridged < 10 - 1e-9)
            {
                seek.InCrossfade.Should().BeTrue();
                // Primary advances continuously along the outgoing tail (8..10)
                seek.PrimarySourceTime.TotalSeconds.Should().BeGreaterThan(prevPrimary - 1e-9);
                seek.PrimarySourceTime.TotalSeconds.Should().BeInRange(8 - 1e-6, 10 + 1e-6);
                // Never snaps to the deleted gap (10..20 exclusive of edges used as tails)
                seek.PrimarySourceTime.TotalSeconds.Should().BeLessThan(10 + 1e-6);
            }

            prevPrimary = seek.PrimarySourceTime.TotalSeconds;
        }
    }

    [Fact]
    public void HardCutSeekDoesNotInventCrossfade()
    {
        var plan = new ExportPlan(
            [
                new TimeRange(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(10)),
                new TimeRange(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30))
            ],
            [new ExportJoin(TransitionMode.HardCut, TimeSpan.Zero)],
            FadeFromBlackSeconds: null,
            FadeToBlackSeconds: null);
        var map = PreviewTimeMap.FromPlan(plan);

        var seek = PreviewSeekState.FromAbridged(plan, map, TimeSpan.FromSeconds(10));
        seek.InCrossfade.Should().BeFalse();
        seek.PrimarySourceTime.TotalSeconds.Should().BeApproximately(20, 1e-6);
    }
}
