using FluentAssertions;
using VideoAbridge.Core;

namespace VideoAbridge.Core.Tests;

public class PreviewComposerTests
{
    private static (ExportPlan Plan, PreviewTimeMap Map) HardCutPlan()
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
        return (plan, PreviewTimeMap.FromPlan(plan));
    }

    [Fact]
    public void EvaluateInDeletedGapJumpsToNextClipStart()
    {
        var (plan, map) = HardCutPlan();
        var state = PreviewComposer.Evaluate(plan, map, TimeSpan.FromSeconds(15));
        state.JumpPrimaryTo.Should().Be(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void EvaluateInSecondClipDoesNotJumpBackToCut()
    {
        var (plan, map) = HardCutPlan();
        // Regression: earlier clip.End checks used to keep snapping back to 20s.
        var state = PreviewComposer.Evaluate(plan, map, TimeSpan.FromSeconds(25));
        state.JumpPrimaryTo.Should().BeNull();
        state.PlayheadSourceTime.TotalSeconds.Should().BeApproximately(25, 1e-6);
        state.InCrossfade.Should().BeFalse();
    }

    [Fact]
    public void EvaluateNearEndOfFirstClipJumpsForward()
    {
        var (plan, map) = HardCutPlan();
        var state = PreviewComposer.Evaluate(plan, map, TimeSpan.FromSeconds(9.99));
        state.JumpPrimaryTo.Should().Be(TimeSpan.FromSeconds(20));
    }
}
