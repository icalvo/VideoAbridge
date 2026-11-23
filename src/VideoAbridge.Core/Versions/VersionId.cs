namespace VideoAbridge.Core.Versions;

/// <summary>Strongly typed abridged-version identity.</summary>
public readonly struct VersionId(Guid value) : IEquatable<VersionId>
{
    public Guid Value { get; } = value;

    public static VersionId New() => new(Guid.NewGuid());

    /// <summary>Wraps a persisted or external guid. Rejects <see cref="Guid.Empty"/>.</summary>
    public static VersionId From(Guid value) =>
        value == Guid.Empty
            ? throw new ArgumentException("Version id cannot be empty.", nameof(value))
            : new(value);

    public bool Equals(VersionId other) => Value.Equals(other.Value);

    public override bool Equals(object? obj) => obj is VersionId other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value.ToString();

    public static bool operator ==(VersionId left, VersionId right) => left.Equals(right);

    public static bool operator !=(VersionId left, VersionId right) => !left.Equals(right);
}
