namespace VideoAbridge.FFmpeg;

/// <summary>Subsampled mono peak envelope (min/max per time bucket).</summary>
public sealed class AudioPeakEnvelope
{
    /// <summary>
    /// Display scale uses this percentile of per-bucket peak |amplitude| so isolated
    /// spikes do not flatten the rest of the waveform (outliers clamp to ±1).
    /// </summary>
    public const float ScalePercentile = 0.99f;

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
        ScaleAbs = ComputeScaleAbs(minima, maxima);
    }

    public TimeSpan Duration { get; }

    public TimeSpan BucketDuration { get; }

    /// <summary>
    /// Robust peak used to normalize <see cref="Sample"/> (99th-percentile |bucket peak|).
    /// </summary>
    public float ScaleAbs { get; }

    public int Count => _minima.Length;

    public bool IsEmpty => Count == 0;

    public ReadOnlySpan<float> Minima => _minima;

    public ReadOnlySpan<float> Maxima => _maxima;

    /// <summary>
    /// Samples the envelope at <paramref name="sourceTime"/> (bucket pick),
    /// scaled by <see cref="ScaleAbs"/> and clamped to ±1.
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
        var scale = 1f / ScaleAbs;
        min = Math.Clamp(_minima[index] * scale, -1f, 1f);
        max = Math.Clamp(_maxima[index] * scale, -1f, 1f);
    }

    private static float ComputeScaleAbs(float[] minima, float[] maxima)
    {
        if (minima.Length == 0)
            return 1f;

        var peaks = new float[minima.Length];
        for (var i = 0; i < minima.Length; i++)
            peaks[i] = Math.Max(Math.Abs(minima[i]), Math.Abs(maxima[i]));

        Array.Sort(peaks);
        var index = (int)Math.Clamp(
            Math.Ceiling(ScalePercentile * peaks.Length) - 1,
            0,
            peaks.Length - 1);
        var scale = peaks[index];
        return scale > 1e-6f ? scale : 1f;
    }
}
