using FluentAssertions;
using VideoAbridge.Core;
using VideoAbridge.FFmpeg;
using Xunit;

namespace VideoAbridge.FFmpeg.Tests;

public class VideoExporterTests
{
    [Fact]
    public void BuildFilterComplex_IncludesXfadeAndAcrossfade()
    {
        var plan = new ExportPlan(
            [
                new KeptClip(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(10)),
                new KeptClip(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30))
            ],
            [new ExportJoin(TransitionMode.Crossfade, 1.5)],
            FadeFromBlackAtStart: true,
            FadeFromBlackSeconds: 1,
            FadeToBlackAtEnd: true,
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
    public void BuildFilterComplex_UsesConcatForHardCut()
    {
        var plan = new ExportPlan(
            [
                new KeptClip(TimeSpan.Zero, TimeSpan.FromSeconds(5)),
                new KeptClip(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(12))
            ],
            [new ExportJoin(TransitionMode.HardCut, 0)],
            FadeFromBlackAtStart: false,
            FadeFromBlackSeconds: 0,
            FadeToBlackAtEnd: false,
            FadeToBlackSeconds: 0);

        var filter = VideoExporter.BuildFilterComplex(plan);
        filter.Should().Contain("concat=");
        filter.Should().NotContain("xfade=");
    }
}
