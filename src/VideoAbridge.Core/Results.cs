namespace VideoAbridge.Core;

/// <summary>
/// Factory helpers for <see cref="Result{T}"/>. Prefer
/// <c>using static VideoAbridge.Core.Results;</c> at call sites.
/// </summary>
public static class Results
{
    public static Result<T> Ok<T>(T value) => new Ok<T>(value);

    public static Result<T> Fail<T>(string code, string message) =>
        new DomainError(code, message);

    public static Result<T> Fail<T>(DomainError error) => error;
}
