using FluentAssertions;
using VideoAbridge.FFmpeg;
using Xunit;

namespace VideoAbridge.FFmpeg.Tests;

public class VideoExporterTests
{
    [Fact]
    public void BuildFilterComplexIncludesXfadeAndAcrossfade()
    {
        var plan = new ExportPlan(
            [
                new TimeRange(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(10)),
                new TimeRange(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30))
            ],
            [new ExportJoin(TransitionMode.Crossfade, TimeSpan.FromSeconds(1.5))],
            FadeFromBlackSeconds: 1,
            FadeToBlackSeconds: 2);

        var filter = VideoExporter.BuildFilterComplex(plan);
        filter.Should().Contain("xfade=");
        filter.Should().Contain("acrossfade=");
        filter.Should().Contain("fade=t=in");
        filter.Should().Contain("afade=t=in");
        filter.Should().Contain("fade=t=out");
        filter.Should().Contain("afade=t=out");
        filter.Should().Contain("[vout]");
        filter.Should().Contain("[aout]");
    }

    [Fact]
    public void BuildFilterComplexUsesConcatForHardCut()
    {
        var plan = new ExportPlan(
            [
                new TimeRange(TimeSpan.Zero, TimeSpan.FromSeconds(5)),
                new TimeRange(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(12))
            ],
            [new ExportJoin(TransitionMode.HardCut, TimeSpan.Zero)],
            FadeFromBlackSeconds: null,
            FadeToBlackSeconds: null);

        var filter = VideoExporter.BuildFilterComplex(plan);
        filter.Should().Contain("concat=");
        filter.Should().NotContain("xfade=");
    }

    [Fact]
    public void BuildArgumentsUsesStreamCopyWhenPlanAllows()
    {
        var plan = new ExportPlan(
            [new TimeRange(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8))],
            [],
            FadeFromBlackSeconds: null,
            FadeToBlackSeconds: null);

        plan.AllowsStreamCopy.Should().BeTrue();

        var args = VideoExporter.BuildArguments("in.mp4", "out.mp4", plan);
        args.Should().Contain("-c");
        args.Should().Contain("copy");
        args.Should().NotContain("-filter_complex");
    }

    [Fact]
    public void BuildArgumentsTranscodesWhenPlanHasFades()
    {
        var plan = new ExportPlan(
            [new TimeRange(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8))],
            [],
            FadeFromBlackSeconds: 0.5,
            FadeToBlackSeconds: null);

        plan.AllowsStreamCopy.Should().BeFalse();

        var args = VideoExporter.BuildArguments("in.mp4", "out.mp4", plan);
        args.Should().Contain("-filter_complex");
        args.Should().NotContain("copy");
    }
}
