namespace VideoAbridge.Core;

/// <summary>LINQ-style helpers for <see cref="TimeSpan"/> sequences (BCL <c>Sum</c> has no TimeSpan overload).</summary>
public static class EnumerableTimeSpanExtensions
{
    public static TimeSpan Sum(this IEnumerable<TimeSpan> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var total = TimeSpan.Zero;
        foreach (var value in source)
            total += value;
        return total;
    }

    public static TimeSpan Sum<TSource>(this IEnumerable<TSource> source, Func<TSource, TimeSpan> selector)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selector);
        var total = TimeSpan.Zero;
        foreach (var item in source)
            total += selector(item);
        return total;
    }
}
