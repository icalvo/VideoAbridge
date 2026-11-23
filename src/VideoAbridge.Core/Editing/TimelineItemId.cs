namespace VideoAbridge.Core.Editing;

/// <summary>
/// Timeline item identity: a <see cref="MarkerId"/> or a <see cref="DeletedRunId"/>.
/// Absence is <c>TimelineItemId?</c> null.
/// </summary>
public readonly union TimelineItemId(MarkerId, DeletedRunId) : IEquatable<TimelineItemId>
{
    public readonly bool Equals(TimelineItemId other) =>
        EqualityComparer<object?>.Default.Equals(Value, other.Value);

    public override readonly bool Equals(object? obj) =>
        obj is TimelineItemId other && Equals(other);

    public override readonly int GetHashCode() =>
        Value?.GetHashCode() ?? 0;

    public static bool operator ==(TimelineItemId left, TimelineItemId right) => left.Equals(right);

    public static bool operator !=(TimelineItemId left, TimelineItemId right) => !left.Equals(right);
}
