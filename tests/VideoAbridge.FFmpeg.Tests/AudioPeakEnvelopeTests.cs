using FluentAssertions;

namespace VideoAbridge.FFmpeg.Tests;

public class AudioPeakEnvelopeTests
{
    [Fact]
    public void SampleUsesPercentileScaleSoIsolatedPeakDoesNotFlattenRest()
    {
        // 100 buckets at 0.1, one spike at 1.0 → 99th percentile is still 0.1
        var minima = new float[100];
        var maxima = new float[100];
        for (var i = 0; i < 100; i++)
        {
            minima[i] = -0.1f;
            maxima[i] = 0.1f;
        }

        maxima[50] = 1f;
        minima[50] = -1f;

        var envelope = new AudioPeakEnvelope(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(20),
            minima,
            maxima);

        envelope.ScaleAbs.Should().BeApproximately(0.1f, 1e-5f);

        envelope.Sample(TimeSpan.FromMilliseconds(10), out var min, out var max);
        min.Should().BeApproximately(-1f, 1e-5f);
        max.Should().BeApproximately(1f, 1e-5f);

        envelope.Sample(TimeSpan.FromSeconds(1.01), out min, out max);
        min.Should().Be(-1f);
        max.Should().Be(1f);
    }
}
