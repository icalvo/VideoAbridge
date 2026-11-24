using FluentAssertions;
using VideoAbridge.Core.Preview;

namespace VideoAbridge.Core.Tests;

public class PreviewOverlayNavigationTests
{
    [Fact]
    public void SeekTargetsApplyLeadInAndSkipZeroFades()
    {
        AbridgedOverlay[] overlays =
        [
            new ExteriorOverlay(
                ExteriorOverlayKind.FadeIn,
                new TimeRange(TimeSpan.Zero, TimeSpan.FromSeconds(1.5))),
            new InteriorOverlay(
                TransitionMode.Crossfade,
                new TimeRange(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10)),
                DeletedRunId.New()),
            new InteriorOverlay(
                TransitionMode.HardCut,
                new TimeRange(TimeSpan.FromSeconds(18), TimeSpan.FromSeconds(18)),
                DeletedRunId.New()),
            new ExteriorOverlay(
                ExteriorOverlayKind.FadeOut,
                new TimeRange(TimeSpan.FromSeconds(27), TimeSpan.FromSeconds(28))),
            // Zero-length fade must not contribute a target.
            new ExteriorOverlay(
                ExteriorOverlayKind.FadeIn,
                new TimeRange(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5))),
        ];

        var targets = PreviewOverlayNavigation.SeekTargets(overlays);
        targets.Should().Equal(
            TimeSpan.Zero, // fade-in at 0 → capped lead-in
            TimeSpan.FromSeconds(6), // xf at 8
            TimeSpan.FromSeconds(16), // cut at 18
            TimeSpan.FromSeconds(25)); // fade-out at 27
    }

    [Fact]
    public void PreviousAndNextStepAmongTargets()
    {
        TimeSpan[] targets =
        [
            TimeSpan.Zero,
            TimeSpan.FromSeconds(6),
            TimeSpan.FromSeconds(16),
        ];

        PreviewOverlayNavigation.Previous(TimeSpan.FromSeconds(6), targets)
            .Should().Be(TimeSpan.Zero);
        PreviewOverlayNavigation.Next(TimeSpan.FromSeconds(6), targets)
            .Should().Be(TimeSpan.FromSeconds(16));
        PreviewOverlayNavigation.Previous(TimeSpan.Zero, targets).Should().BeNull();
        PreviewOverlayNavigation.Next(TimeSpan.FromSeconds(16), targets).Should().BeNull();
    }

    [Fact]
    public void DeduplicatesIdenticalSeekTargets()
    {
        AbridgedOverlay[] overlays =
        [
            new InteriorOverlay(
                TransitionMode.HardCut,
                new TimeRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)),
                DeletedRunId.New()),
            new InteriorOverlay(
                TransitionMode.HardCut,
                new TimeRange(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(1.5)),
                DeletedRunId.New()),
        ];

        // Both clamp to 0 with a 2s lead-in.
        PreviewOverlayNavigation.SeekTargets(overlays).Should().Equal(TimeSpan.Zero);
    }
}
