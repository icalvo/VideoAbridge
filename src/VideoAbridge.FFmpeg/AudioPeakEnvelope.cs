namespace VideoAbridge.FFmpeg;

/// <summary>Subsampled mono peak envelope (min/max per time bucket).</summary>
public sealed class AudioPeakEnvelope
{
    public static AudioPeakEnvelope Empty { get; } = new(
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(20),
        [],
        []);

    private readonly float[] _minima;
    private readonly float[] _maxima;

    public AudioPeakEnvelope(
        TimeSpan duration,
        TimeSpan bucketDuration,
        float[] minima,
        float[] maxima)
    {
        ArgumentNullException.ThrowIfNull(minima);
        ArgumentNullException.ThrowIfNull(maxima);
        if (minima.Length != maxima.Length)
            throw new ArgumentException("Minima and maxima lengths must match.");

        Duration = duration;
        BucketDuration = bucketDuration <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(20)
            : bucketDuration;
        _minima = minima;
        _maxima = maxima;
        PeakAbs = ComputePeakAbs(minima, maxima);
    }

    public TimeSpan Duration { get; }

    public TimeSpan BucketDuration { get; }

    /// <summary>Largest |sample| in the envelope; used to peak-normalize <see cref="Sample"/>.</summary>
    public float PeakAbs { get; }

    public int Count => _minima.Length;

    public bool IsEmpty => Count == 0;

    /// <summary>
    /// Samples the envelope at <paramref name="sourceTime"/> (bucket pick),
    /// peak-normalized so the loudest bucket is ±1.
    /// </summary>
    public void Sample(TimeSpan sourceTime, out float min, out float max)
    {
        if (IsEmpty)
        {
            min = 0;
            max = 0;
            return;
        }

        var bucketSeconds = Math.Max(BucketDuration.TotalSeconds, 1e-9);
        var index = (int)Math.Floor(sourceTime.TotalSeconds / bucketSeconds);
        index = Math.Clamp(index, 0, Count - 1);
        var scale = 1f / PeakAbs;
        min = _minima[index] * scale;
        max = _maxima[index] * scale;
    }

    private static float ComputePeakAbs(float[] minima, float[] maxima)
    {
        float peak = 0;
        for (var i = 0; i < minima.Length; i++)
        {
            var a = Math.Abs(minima[i]);
            var b = Math.Abs(maxima[i]);
            if (a > peak)
                peak = a;
            if (b > peak)
                peak = b;
        }

        return peak > 1e-6f ? peak : 1f;
    }
}
