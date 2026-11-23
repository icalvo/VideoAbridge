using System.IO;
using FluentAssertions;
using VideoAbridge.FFmpeg;
using VideoAbridge.Services;

namespace VideoAbridge.Tests;

public class AudioPeakCacheServiceTests
{
    [Fact]
    public void SaveAndTryLoadRoundTrip()
    {
        using var dir = new TempDir();
        var video = dir.WriteFile("clip.bin", CreateBytes(200));
        var duration = TimeSpan.FromSeconds(2);
        var bucket = AudioPeakExtractor.DefaultBucketDuration;
        var cache = new AudioPeakCacheService(Path.Combine(dir.Path, "WaveformCache"));

        var minima = new float[] { -0.5f, -0.25f, 0f, -0.1f };
        var maxima = new float[] { 0.5f, 0.75f, 0.1f, 0.2f };
        var envelope = new AudioPeakEnvelope(duration, bucket, minima, maxima);

        cache.Save(video, duration, envelope, AudioPeakExtractor.SampleRate);

        var loaded = cache.TryLoad(video, duration, bucket, AudioPeakExtractor.SampleRate);
        loaded.Should().NotBeNull();
        loaded.IsEmpty.Should().BeFalse();
        loaded.Duration.Should().Be(duration);
        loaded.BucketDuration.Should().Be(bucket);
        loaded.Count.Should().Be(4);
        loaded.Minima.ToArray().Should().Equal(minima);
        loaded.Maxima.ToArray().Should().Equal(maxima);

        Directory.GetFiles(cache.CacheDirectory, "*.peaks").Should().ContainSingle();
    }

    [Fact]
    public void SaveSkipsEmptyEnvelope()
    {
        using var dir = new TempDir();
        var video = dir.WriteFile("clip.bin", CreateBytes(50));
        var cache = new AudioPeakCacheService(Path.Combine(dir.Path, "WaveformCache"));

        cache.Save(
            video,
            TimeSpan.FromSeconds(1),
            AudioPeakEnvelope.Empty,
            AudioPeakExtractor.SampleRate);

        Directory.GetFiles(cache.CacheDirectory, "*.peaks").Should().BeEmpty();
    }

    [Fact]
    public void TryLoadReturnsNullWhenSampleRateDoesNotMatch()
    {
        using var dir = new TempDir();
        var video = dir.WriteFile("clip.bin", CreateBytes(80));
        var duration = TimeSpan.FromSeconds(1);
        var bucket = AudioPeakExtractor.DefaultBucketDuration;
        var cache = new AudioPeakCacheService(Path.Combine(dir.Path, "WaveformCache"));
        var envelope = new AudioPeakEnvelope(
            duration,
            bucket,
            [-0.2f],
            [0.3f]);

        cache.Save(video, duration, envelope, AudioPeakExtractor.SampleRate);

        cache.TryLoad(video, duration, bucket, sampleRate: 16000).Should().BeNull();
    }

    [Fact]
    public void TryLoadReturnsNullWhenBucketDurationDoesNotMatch()
    {
        using var dir = new TempDir();
        var video = dir.WriteFile("clip.bin", CreateBytes(80));
        var duration = TimeSpan.FromSeconds(1);
        var cache = new AudioPeakCacheService(Path.Combine(dir.Path, "WaveformCache"));
        var envelope = new AudioPeakEnvelope(
            duration,
            AudioPeakExtractor.DefaultBucketDuration,
            [-0.2f],
            [0.3f]);

        cache.Save(video, duration, envelope, AudioPeakExtractor.SampleRate);

        cache.TryLoad(
                video,
                duration,
                TimeSpan.FromMilliseconds(40),
                AudioPeakExtractor.SampleRate)
            .Should().BeNull();
    }

    private static byte[] CreateBytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = (byte)(i & 0xFF);
        return bytes;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "VideoAbridgeTests_" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public string WriteFile(string name, byte[] content)
        {
            var full = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(full, content);
            return full;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
