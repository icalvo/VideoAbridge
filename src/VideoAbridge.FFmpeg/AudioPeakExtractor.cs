using System.Globalization;

namespace VideoAbridge.FFmpeg;

/// <summary>
/// Builds a compact peak envelope by streaming low-rate mono PCM from ffmpeg (no full WAV on disk).
/// </summary>
public sealed class AudioPeakExtractor(string? ffmpegPath = null)
{
    public const int SampleRate = 8000;

    public static readonly TimeSpan DefaultBucketDuration = TimeSpan.FromMilliseconds(20);

    private readonly string _ffmpegPath = ffmpegPath ?? FFmpegLocator.FindFFmpeg()
        ?? throw new InvalidOperationException("ffmpeg was not found.");

    public async Task<AudioPeakEnvelope> ExtractAsync(
        string videoPath,
        TimeSpan? bucketDuration = null,
        TimeSpan? duration = null,
        IProgress<double>? progress = null,
        Action<string>? onLogLine = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);

        var bucket = bucketDuration is { } b && b > TimeSpan.Zero ? b : DefaultBucketDuration;
        var samplesPerBucket = Math.Max(1, (int)Math.Round(SampleRate * bucket.TotalSeconds));
        var totalSeconds = duration is { } d && d > TimeSpan.Zero ? d.TotalSeconds : 0;

        var result = await ProcessRunner.RunBinaryStdoutAsync(
            _ffmpegPath,
            [
                "-hide_banner",
                "-nostdin",
                "-i", videoPath,
                "-vn",
                "-sn",
                "-dn",
                "-map", "a:0?",
                "-ac", "1",
                "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
                "-f", "s16le",
                "-acodec", "pcm_s16le",
                "pipe:1"
            ],
            line =>
            {
                onLogLine?.Invoke(line);
                ReportProgressFromTimeToken(progress, line, totalSeconds);
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            // No audio stream / decode failure → empty overlay.
            return AudioPeakEnvelope.Empty;
        }

        var pcm = result.StandardOutput.Span;
        if (pcm.Length < 2)
            return AudioPeakEnvelope.Empty;

        var sampleCount = pcm.Length / 2;
        var bucketCount = Math.Max(1, (sampleCount + samplesPerBucket - 1) / samplesPerBucket);
        var minima = new float[bucketCount];
        var maxima = new float[bucketCount];

        for (var bucketIndex = 0; bucketIndex < bucketCount; bucketIndex++)
        {
            var start = bucketIndex * samplesPerBucket;
            var end = Math.Min(sampleCount, start + samplesPerBucket);
            short minSample = short.MaxValue;
            short maxSample = short.MinValue;
            for (var i = start; i < end; i++)
            {
                var sample = BitConverter.ToInt16(pcm.Slice(i * 2, 2));
                if (sample < minSample)
                    minSample = sample;
                if (sample > maxSample)
                    maxSample = sample;
            }

            if (end <= start)
            {
                minima[bucketIndex] = 0;
                maxima[bucketIndex] = 0;
            }
            else
            {
                minima[bucketIndex] = minSample / 32768f;
                maxima[bucketIndex] = maxSample / 32768f;
            }
        }

        var envelopeDuration = TimeSpan.FromSeconds(sampleCount / (double)SampleRate);
        progress?.Report(1);
        return new AudioPeakEnvelope(envelopeDuration, bucket, minima, maxima);
    }

    private static void ReportProgressFromTimeToken(
        IProgress<double>? progress,
        string line,
        double totalSeconds)
    {
        if (progress is null || totalSeconds <= 0)
            return;

        var idx = line.IndexOf("time=", StringComparison.Ordinal);
        if (idx < 0)
            return;

        var span = line.AsSpan(idx + 5);
        var end = span.IndexOfAny([' ', '\t']);
        var token = end < 0 ? span : span[..end];
        if (token.IsEmpty || token.Equals("N/A", StringComparison.OrdinalIgnoreCase))
            return;

        double currentSeconds;
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out currentSeconds))
        {
            // ok
        }
        else if (TimeSpan.TryParse(token, CultureInfo.InvariantCulture, out var current))
        {
            currentSeconds = current.TotalSeconds;
        }
        else
        {
            return;
        }

        progress.Report(Math.Clamp(currentSeconds / totalSeconds, 0, 1));
    }
}
