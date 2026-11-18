namespace VideoAbridge.Core.Preview;

/// <summary>
/// Overlay on the abridged (export) timeline ruler: edge fades, or interior join cuts/crossfades.
/// </summary>
public readonly union AbridgedOverlay(ExteriorOverlay, InteriorOverlay) : IEquatable<AbridgedOverlay>
{
    public TimeRange Range => this switch
    {
        ExteriorOverlay exterior => exterior.Range,
        InteriorOverlay interior => interior.Range
    };

    public readonly bool Equals(AbridgedOverlay other) =>
        EqualityComparer<object?>.Default.Equals(Value, other.Value);

    public override readonly bool Equals(object? obj) =>
        obj is AbridgedOverlay other && Equals(other);

    public override readonly int GetHashCode() =>
        Value?.GetHashCode() ?? 0;

    public static bool operator ==(AbridgedOverlay left, AbridgedOverlay right) => left.Equals(right);

    public static bool operator !=(AbridgedOverlay left, AbridgedOverlay right) => !left.Equals(right);
}
