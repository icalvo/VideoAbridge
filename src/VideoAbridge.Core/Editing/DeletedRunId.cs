namespace VideoAbridge.Core.Editing;

/// <summary>
/// Strongly typed deleted-run identity. Leading/trailing/full are stable edge ids;
/// interior runs use <see cref="New"/> (also stored on <see cref="JoinTransition"/>).
/// </summary>
public readonly struct DeletedRunId(Guid value) : IEquatable<DeletedRunId>
{
    public Guid Value { get; } = value;

    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Stable id for a deleted run that starts at t=0 (not interior).</summary>
    public static DeletedRunId Leading { get; } = new(new Guid("00000000-0000-0000-0000-000000000003"));

    /// <summary>Stable id for a deleted run that ends at Duration (not interior).</summary>
    public static DeletedRunId Trailing { get; } = new(new Guid("00000000-0000-0000-0000-000000000004"));

    /// <summary>Stable id when the entire timeline is deleted.</summary>
    public static DeletedRunId Full { get; } = new(new Guid("00000000-0000-0000-0000-000000000005"));

    public static DeletedRunId New() => new(Guid.NewGuid());

    /// <summary>Wraps a persisted or external guid. Rejects <see cref="Guid.Empty"/>.</summary>
    public static DeletedRunId From(Guid value) =>
        value == Guid.Empty
            ? throw new ArgumentException("Deleted-run id cannot be empty.", nameof(value))
            : new(value);

    public bool Equals(DeletedRunId other) => Value.Equals(other.Value);

    public override bool Equals(object? obj) => obj is DeletedRunId other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value.ToString();

    public static bool operator ==(DeletedRunId left, DeletedRunId right) => left.Equals(right);

    public static bool operator !=(DeletedRunId left, DeletedRunId right) => !left.Equals(right);
}
