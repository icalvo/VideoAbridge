namespace VideoAbridge.Core.Outcome;

/// <summary>Success or <see cref="DomainError"/>, consumed via Match / Map / Bind.</summary>
public readonly union Result<T>(Ok<T>, DomainError) : IEquatable<Result<T>>
{
    public readonly TResult Match<TResult>(Func<T, TResult> onSuccess, Func<DomainError, TResult> onError)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onError);
        return this switch
        {
            Ok<T> ok => onSuccess(ok.Value),
            DomainError error => onError(error)
        };
    }

    public readonly void Match(Action<T> onSuccess, Action<DomainError> onError)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onError);
        Match(
            value =>
            {
                onSuccess(value);
                return true;
            },
            error =>
            {
                onError(error);
                return true;
            });
    }

    public readonly Result<TOut> Map<TOut>(Func<T, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return Match(value => Results.Ok(map(value)), Results.Fail<TOut>);
    }

    public readonly Result<T> MapError(Func<DomainError, DomainError> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return Match(Results.Ok, error => Results.Fail<T>(map(error)));
    }

    public readonly Result<TOut> Bind<TOut>(Func<T, Result<TOut>> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return Match(bind, Results.Fail<TOut>);
    }

    public readonly bool Equals(Result<T> other) =>
        EqualityComparer<object?>.Default.Equals(Value, other.Value);

    public override readonly bool Equals(object? obj) =>
        obj is Result<T> other && Equals(other);

    public override readonly int GetHashCode() =>
        Value?.GetHashCode() ?? 0;

    public static bool operator ==(Result<T> left, Result<T> right) => left.Equals(right);

    public static bool operator !=(Result<T> left, Result<T> right) => !left.Equals(right);
}
