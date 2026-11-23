namespace VideoAbridge.Core;

/// <summary>Common <see cref="TimeSpan"/> comparisons without dropping to seconds.</summary>
public static class TimeSpans
{
    public static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;

    public static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    public static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (min > max)
            (min, max) = (max, min);
        if (value < min)
            return min;
        if (value > max)
            return max;
        return value;
    }
}
