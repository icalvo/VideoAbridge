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
    }

    public TimeSpan Duration { get; }

    public TimeSpan BucketDuration { get; }

    public int Count => _minima.Length;

    public bool IsEmpty => Count == 0;

    /// <summary>Samples the envelope at <paramref name="sourceTime"/> (linear bucket pick).</summary>
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
        min = _minima[index];
        max = _maxima[index];
    }
}
