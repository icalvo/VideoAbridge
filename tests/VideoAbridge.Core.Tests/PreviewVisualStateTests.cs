using FluentAssertions;

namespace VideoAbridge.Core.Tests;

public class PreviewVisualStateTests
{
    private static (ExportPlan Plan, PreviewTimeMap Map) HardCutPlan()
    {
        var plan = new ExportPlan(
            [
                new TimeRange(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(10)),
                new TimeRange(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30))
            ],
            [new ExportJoin(TransitionMode.HardCut, 0)],
            FadeFromBlackSeconds: null,
            FadeToBlackSeconds: null);
        return (plan, PreviewTimeMap.FromPlan(plan));
    }

    [Fact]
    public void EvaluateInDeletedGapJumpsToNextClipStart()
    {
        var (plan, map) = HardCutPlan();
        var state = PreviewVisualState.Evaluate(plan, map, TimeSpan.FromSeconds(15));
        state.JumpPrimaryTo.Should().Be(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void EvaluateInSecondClipDoesNotJumpBackToCut()
    {
        var (plan, map) = HardCutPlan();
        // Regression: earlier clip.End checks used to keep snapping back to 20s.
        var state = PreviewVisualState.Evaluate(plan, map, TimeSpan.FromSeconds(25));
        state.JumpPrimaryTo.Should().BeNull();
        state.PlayheadSourceTime.TotalSeconds.Should().BeApproximately(25, 1e-6);
        state.InCrossfade.Should().BeFalse();
    }

    [Fact]
    public void EvaluateNearEndOfFirstClipJumpsForward()
    {
        var (plan, map) = HardCutPlan();
        var state = PreviewVisualState.Evaluate(plan, map, TimeSpan.FromSeconds(9.99));
        state.JumpPrimaryTo.Should().Be(TimeSpan.FromSeconds(20));
    }
}
