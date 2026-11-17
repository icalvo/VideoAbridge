namespace VideoAbridge.Core;

public readonly record struct DomainError(string Code, string Message);

public readonly record struct Ok<T>(T Value);

/// <summary>
/// Success or <see cref="DomainError"/> — C# 15 union with exhaustive pattern matching.
/// </summary>
public readonly union Result<T>(Ok<T>, DomainError) : IEquatable<Result<T>>
{
    public readonly bool IsSuccess => Value is Ok<T>;

    /// <summary>Success payload; only valid when <see cref="IsSuccess"/> is true.</summary>
    public readonly T OkValue => Value is Ok<T> ok
        ? ok.Value
        : throw new InvalidOperationException("Result has no success value.");

    public readonly DomainError? Error => Value is DomainError error ? error : null;

    public readonly bool Equals(Result<T> other) =>
        EqualityComparer<object?>.Default.Equals(Value, other.Value);

    public override readonly bool Equals(object? obj) =>
        obj is Result<T> other && Equals(other);

    public override readonly int GetHashCode() =>
        Value?.GetHashCode() ?? 0;

    public static bool operator ==(Result<T> left, Result<T> right) => left.Equals(right);

    public static bool operator !=(Result<T> left, Result<T> right) => !left.Equals(right);
}
