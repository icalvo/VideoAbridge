namespace VideoAbridge.Core.Editing;

/// <summary>
/// Strongly typed marker identity. Start/end bounds are virtual markers used as
/// range endpoints for delete/restore; they never appear in <see cref="Timeline.Markers"/>.
/// </summary>
public readonly struct MarkerId(Guid value) : IEquatable<MarkerId>
{
    public Guid Value { get; } = value;

    /// <summary>Virtual boundary at t=0 for range operations.</summary>
    public static MarkerId StartBound { get; } = new(new Guid("00000000-0000-0000-0000-000000000001"));

    /// <summary>Virtual boundary at timeline end for range operations.</summary>
    public static MarkerId EndBound { get; } = new(new Guid("00000000-0000-0000-0000-000000000002"));

    public static MarkerId New() => new(Guid.NewGuid());

    /// <summary>Wraps a persisted or external guid. Rejects <see cref="Guid.Empty"/>.</summary>
    public static MarkerId From(Guid value) =>
        value == Guid.Empty
            ? throw new ArgumentException("Marker id cannot be empty.", nameof(value))
            : new(value);

    public bool Equals(MarkerId other) => Value.Equals(other.Value);

    public override bool Equals(object? obj) => obj is MarkerId other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value.ToString();

    public static bool operator ==(MarkerId left, MarkerId right) => left.Equals(right);

    public static bool operator !=(MarkerId left, MarkerId right) => !left.Equals(right);
}
